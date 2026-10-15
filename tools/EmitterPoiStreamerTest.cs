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
/// M1-5 emitter POI streamer (docs/emitter-poi-streaming-spec.md section A): the pure state machine with synthetic
/// observations (part A) and the node on the stand-in fixture (part B). Simulated clock at 1/60 s.
/// </summary>
public partial class EmitterPoiStreamerTest : RefCounted
{
    private const double Dt = 1d / 60d;
    private const double Load = 14000d, Unload = 16000d;
    private static readonly SiteKind[] AllSlots = { SiteKind.Regular, SiteKind.Regular, SiteKind.Main };

    public int Run(bool unused)
    {
        _ = unused;
        var failures = new List<string>();
        Node3D? fixture = null;
        try
        {
            RunPartA(failures);
            SceneTree tree = (Engine.GetMainLoop() as SceneTree) ?? throw new InvalidOperationException("scene tree unavailable");
            using RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen17(1337);
            using INaturalHeightSource source = EmitterSiteHeightSources.Create(sampler);
            EmitterSiteRegistry registry = EmitterSiteRegistry.GetOrBuild(EmitterSiteRecipe.Default(1337), source);
            var placements = new EmitterSitePlacementSource(registry);
            fixture = new Node3D { Name = "EmitterPoiStreamerFixture" };
            tree.Root.AddChild(fixture);
            RunPartB(fixture, placements, failures);
            RunLoadFailures(fixture, placements, failures);
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
        if (failures.Count != 0) { Console.Error.WriteLine("EMITTER_POI_STREAMER=FAIL " + string.Join(" | ", failures.Take(25))); return 1; }
        Console.WriteLine("EMITTER_POI_STREAMER=PASS");
        return 0;
    }

    // ---------------------------------------------------------------- Part A: core

    static EmitterPoiStreamerConfig Cfg(bool enabled = true) => new(enabled, Load, 32000d, Unload - Load, 1d, 1500d, 500d);

    static EmitterPoiObservation Obs(int site, double d, double ground = double.PositiveInfinity, SiteKind kind = SiteKind.Regular)
        => new(site, kind, d, double.IsPositiveInfinity(ground) ? d : ground);

    static List<EmitterPoiSlotEvent> Update(EmitterPoiStreamerCore core, double now, params EmitterPoiObservation[] obs)
    {
        var events = new List<EmitterPoiSlotEvent>();
        core.Update(now, obs, events);
        return events;
    }

    static int Count(List<EmitterPoiSlotEvent> events, EmitterPoiSlotEventKind kind) => events.Count(e => e.Kind == kind);

    static void RunPartA(List<string> failures)
    {
        const double C = 31_415_936d;
        // Metric and wrap helpers.
        if (Math.Abs(EmitterPoiStreamerCore.Metric(3000, 4000, 0, 0, SiteKind.Regular) - Math.Sqrt(25e6 + 900d * 900d)) > 1e-6) failures.Add("regular metric");
        if (Math.Abs(EmitterPoiStreamerCore.Metric(0, 0, 100, 50, SiteKind.Main) - 2050d) > 1e-9) failures.Add("main metric");
        if (Math.Abs(EmitterPoiStreamerCore.GroundMetric(3, 4, 10, 10) - 5d) > 1e-9) failures.Add("ground metric");
        if (Math.Abs(EmitterPoiStreamerCore.SignedWrap(C - 5, C) + 5) > 1e-6 || Math.Abs(EmitterPoiStreamerCore.SignedWrap(-(C - 5), C) - 5) > 1e-6
            || Math.Abs(EmitterPoiStreamerCore.SignedWrap(3 * C + 7, C) - 7) > 1e-6 || Math.Abs(EmitterPoiStreamerCore.SignedWrap(10 - (C - 10), C) - 20) > 1e-6
            || EmitterPoiStreamerCore.SignedWrap(0, C) != 0 || Math.Abs(EmitterPoiStreamerCore.SignedWrap(C / 2 + 1, C) + (C / 2 - 1)) > 1e-6)
            failures.Add("SignedWrap");

        // Approach: assigned on the first frame with d < load, not before.
        {
            var core = new EmitterPoiStreamerCore(AllSlots, Cfg());
            int firstAssign = -1;
            for (int frame = 0; frame < 400; frame++)
            {
                double d = 20000 - 100d * frame;
                var events = Update(core, frame * Dt, Obs(5, d));
                if (Count(events, EmitterPoiSlotEventKind.Assign) > 0) { firstAssign = frame; break; }
            }
            // d = 20000 - 100 f < 14000  <=>  f > 60
            if (firstAssign != 61) failures.Add($"approach assigned at frame {firstAssign}");
            if (core.FindSlotForSite(5) != 0) failures.Add("approach used slot " + core.FindSlotForSite(5));
        }

        // Oscillation 13..17 km, period 2 s, 120 s.
        {
            var core = new EmitterPoiStreamerCore(AllSlots, Cfg());
            int assigns = 0, releases = 0, frames = 120 * 60;
            double heldAt = -1, shortest = double.MaxValue;
            for (int frame = 0; frame <= frames; frame++)
            {
                double now = frame * Dt;
                double d = 15000 - 2000 * Math.Cos(Math.PI * now);
                foreach (EmitterPoiSlotEvent e in Update(core, now, Obs(3, d)))
                {
                    if (e.Kind == EmitterPoiSlotEventKind.Assign) { assigns++; heldAt = now; }
                    else if (e.Kind == EmitterPoiSlotEventKind.Release) { releases++; shortest = Math.Min(shortest, now - heldAt); }
                }
                if (core.ActiveCount > 1) failures.Add("oscillation: more than one active slot");
            }
            if (assigns > 61 || assigns < 55) failures.Add($"oscillation assigns {assigns}");
            if (releases > assigns || assigns - releases > 1) failures.Add($"oscillation releases {releases} vs assigns {assigns}");
            if (shortest < 1d - 1e-6) failures.Add($"oscillation thrash: shortest hold {shortest}");
        }

        // Dwell: release on exactly the first frame where now - assigned >= 1 s and d > unload.
        {
            var core = new EmitterPoiStreamerCore(AllSlots, Cfg());
            Update(core, 7 * Dt, Obs(1, 13000));
            int releasedAt = -1;
            for (int frame = 8; frame < 200; frame++)
            {
                var events = Update(core, frame * Dt, Obs(1, 16500));
                if (Count(events, EmitterPoiSlotEventKind.Release) > 0) { releasedAt = frame; break; }
            }
            if (releasedAt != 67) failures.Add($"dwell release at frame {releasedAt}, expected 67");
            var held = new EmitterPoiStreamerCore(AllSlots, Cfg());
            Update(held, 0, Obs(1, 13000));
            if (Count(Update(held, 59 * Dt, Obs(1, 16500)), EmitterPoiSlotEventKind.Release) != 0) failures.Add("released before the dwell");
            if (Count(Update(held, 61 * Dt, Obs(1, 15900)), EmitterPoiSlotEventKind.Release) != 0) failures.Add("released inside the unload radius");
            if (Count(Update(held, 62 * Dt, Obs(1, 16000)), EmitterPoiSlotEventKind.Release) != 0) failures.Add("released at exactly the unload radius");
            if (Count(Update(held, 63 * Dt), EmitterPoiSlotEventKind.Release) != 1) failures.Add("absent site did not release");
        }

        // Collision: on below 1.5 km, off above 2.0 km, held between, not dwell-gated, evaluated on the assign frame.
        {
            var core = new EmitterPoiStreamerCore(AllSlots, Cfg());
            var first = Update(core, 0, Obs(2, 1400, 1400));
            if (first.Count != 2 || first[0].Kind != EmitterPoiSlotEventKind.Assign || first[1].Kind != EmitterPoiSlotEventKind.CollisionOn) failures.Add("collision on the assign frame");
            bool Toggle(double t, double ground, EmitterPoiSlotEventKind? expected)
            {
                var events = Update(core, t, Obs(2, 3000, ground));
                if (expected == null) return events.Count == 0;
                return events.Count == 1 && events[0].Kind == expected;
            }
            if (!Toggle(Dt, 1600, null)) failures.Add("collision held at 1.6 km");
            if (!Toggle(2 * Dt, 2000, null)) failures.Add("collision held at 2.0 km");
            if (!Toggle(3 * Dt, 2001, EmitterPoiSlotEventKind.CollisionOff)) failures.Add("collision off above 2.0 km");
            if (!Toggle(4 * Dt, 1700, null)) failures.Add("collision stayed off at 1.7 km");
            if (!Toggle(5 * Dt, 1499, EmitterPoiSlotEventKind.CollisionOn)) failures.Add("collision on below 1.5 km");
            if (!core.Slots[0].Collision) failures.Add("collision state flag");
        }

        // Two sites take R0 and R1 (nearest first); a third is not assigned.
        {
            var core = new EmitterPoiStreamerCore(AllSlots, Cfg());
            var events = Update(core, 0, Obs(10, 9000), Obs(11, 5000), Obs(12, 12000));
            var assigns = events.Where(e => e.Kind == EmitterPoiSlotEventKind.Assign).ToList();
            if (assigns.Count != 2 || assigns[0].Site != 11 || assigns[0].Slot != 0 || assigns[1].Site != 10 || assigns[1].Slot != 1) failures.Add("two sites did not take R0/R1 nearest first");
            if (core.FindSlotForSite(12) != -1) failures.Add("third site was assigned");

            // Eviction: both slots held only by dwell; a closer third site evicts the farthest one that is past unload.
            var evict = new EmitterPoiStreamerCore(AllSlots, Cfg());
            Update(evict, 0, Obs(1, 13000), Obs(2, 13500));
            var none = Update(evict, 0.5, Obs(1, 15000), Obs(2, 15500), Obs(3, 8000));
            if (none.Count != 0) failures.Add("evicted a slot that is still inside its unload radius");
            var ev = Update(evict, 0.5 + Dt, Obs(1, 16500), Obs(2, 17000), Obs(3, 8000));
            if (ev.Count < 2 || ev[0].Kind != EmitterPoiSlotEventKind.Release || ev[0].Site != 2 || ev[1].Kind != EmitterPoiSlotEventKind.Assign || ev[1].Site != 3 || ev[1].Slot != ev[0].Slot)
                failures.Add("eviction picked the wrong slot or ordering: " + string.Join(",", ev.Select(e => $"{e.Kind}:{e.Slot}:{e.Site}")));
            if (evict.FindSlotForSite(1) != 0) failures.Add("eviction disturbed the other slot");
        }

        // Teleport with dwell met: release and assign in the same frame on the same slot; within the dwell the spare slot is used.
        {
            var core = new EmitterPoiStreamerCore(AllSlots, Cfg());
            Update(core, 0, Obs(1, 5000));
            var met = Update(core, 2d, Obs(2, 5000));
            if (met.Count < 2 || met[0] != new EmitterPoiSlotEvent(EmitterPoiSlotEventKind.Release, 0, 1) || met[1] != new EmitterPoiSlotEvent(EmitterPoiSlotEventKind.Assign, 0, 2))
                failures.Add("teleport with dwell met: " + string.Join(",", met.Select(e => $"{e.Kind}:{e.Slot}:{e.Site}")));
            var spare = new EmitterPoiStreamerCore(AllSlots, Cfg());
            Update(spare, 0, Obs(1, 5000));
            var within = Update(spare, 0.2, Obs(2, 5000));
            if (within.Count != 1 || within[0] != new EmitterPoiSlotEvent(EmitterPoiSlotEventKind.Assign, 1, 2) || spare.FindSlotForSite(1) != 0) failures.Add("teleport within the dwell did not use the spare slot");
        }

        // Main: 31 km goes to M0, a regular at 31 km does not, release past 34 km.
        {
            var core = new EmitterPoiStreamerCore(AllSlots, Cfg());
            var events = Update(core, 0, Obs(594, 31000, kind: SiteKind.Main), Obs(4, 31000));
            if (core.FindSlotForSite(594) != 2) failures.Add("main site did not take M0");
            if (core.FindSlotForSite(4) != -1) failures.Add("regular site at 31 km was assigned");
            if (events.Count(e => e.Kind == EmitterPoiSlotEventKind.Assign) != 1) failures.Add("main assign count");
            if (Count(Update(core, 2, Obs(594, 33999, kind: SiteKind.Main)), EmitterPoiSlotEventKind.Release) != 0) failures.Add("main released inside 34 km");
            if (Count(Update(core, 2 + Dt, Obs(594, 34001, kind: SiteKind.Main)), EmitterPoiSlotEventKind.Release) != 1) failures.Add("main did not release past 34 km");
        }

        // Disable releases everything, enable reassigns.
        {
            var core = new EmitterPoiStreamerCore(AllSlots, Cfg());
            Update(core, 0, Obs(1, 5000), Obs(2, 6000), Obs(594, 7000, kind: SiteKind.Main));
            if (core.ActiveCount != 3) failures.Add("three slots not active");
            core.Config = Cfg(false);
            var events = Update(core, Dt, Obs(1, 5000), Obs(2, 6000), Obs(594, 7000, kind: SiteKind.Main));
            if (core.ActiveCount != 0 || events.Count != 3 || events.Any(e => e.Kind != EmitterPoiSlotEventKind.Release)) failures.Add("disable did not release all");
            core.Config = Cfg();
            if (Update(core, 2 * Dt, Obs(1, 5000)).Count(e => e.Kind == EmitterPoiSlotEventKind.Assign) != 1) failures.Add("re-enable did not assign");
        }

        // Without a main slot (failed kind) main is never assigned.
        {
            var core = new EmitterPoiStreamerCore(new[] { SiteKind.Regular, SiteKind.Regular }, Cfg());
            Update(core, 0, Obs(594, 1000, kind: SiteKind.Main), Obs(1, 5000));
            if (core.FindSlotForSite(594) != -1 || core.FindSlotForSite(1) != 0) failures.Add("core without a main slot");
            var mainOnly = new EmitterPoiStreamerCore(new[] { SiteKind.Main }, Cfg());
            Update(mainOnly, 0, Obs(594, 1000, kind: SiteKind.Main), Obs(1, 5000));
            if (mainOnly.FindSlotForSite(594) != 0 || mainOnly.FindSlotForSite(1) != -1) failures.Add("core without regular slots");
        }
    }

    // ---------------------------------------------------------------- Part B: node

    sealed class Rig
    {
        public Node3D Root = null!;
        public VoxelChunkGridManager Grid = null!;
        public EmitterStandInLayer StandIns = null!;
        public EmitterPoiStreamer Streamer = null!;
        public int Frame;
        public double Now => Frame * Dt;
    }

    static Rig MakeRig(Node3D fixture, EmitterSitePlacementSource placements, string name, Action<EmitterPoiStreamer>? configure = null)
    {
        var rig = new Rig { Root = new Node3D { Name = name } };
        fixture.AddChild(rig.Root);
        rig.Grid = new VoxelChunkGridManager
        {
            Name = "ChunkGridManager",
            BuildOnReady = false,
            DormantEditableBackend = true,
            EnableChunkStreaming = false,
            UseDeterministicTerrainGeneration = true,
            EnableCanonicalFloatingOriginAnchor = true,
            TerrainSettings = new VoxelTerrainSettings()
        };
        rig.Root.AddChild(rig.Grid);
        rig.StandIns = new EmitterStandInLayer { Name = "EmitterStandIns", AutoBind = false };
        rig.Root.AddChild(rig.StandIns);
        rig.StandIns.Bind(rig.Grid, placements);
        rig.Streamer = new EmitterPoiStreamer { Name = "EmitterSites", AutoBind = false, SynchronousLoad = true, UseStagedStartup = true };
        configure?.Invoke(rig.Streamer);
        rig.Root.AddChild(rig.Streamer);
        return rig;
    }

    static bool WarmPool(Rig rig, List<string> failures, string label, int maxCalls = 40, bool delay = false)
    {
        int calls = 0;
        while (!rig.Streamer.PoolReady && calls++ < maxCalls)
        {
            rig.Streamer.AdvanceStartup();
            if (delay) OS.DelayMsec(20);
        }
        if (!rig.Streamer.PoolReady) { failures.Add(label + ": pool never became ready"); return false; }
        return true;
    }

    static void StepTo(Rig rig, in RingCanonicalPosition observer, int frames = 1)
    {
        for (int i = 0; i < frames; i++) { rig.Frame++; rig.Streamer.Step(rig.Now, observer); }
    }

    /// <summary>Observer whose mid-height metric to <paramref name="site"/> is <paramref name="distance"/>, offset along the ring.</summary>
    static RingCanonicalPosition ObserverAt(EmitterSitePlacementSource placements, int site, double distance, double eye = 1.7)
    {
        EmitterSitePlacement s = placements.GetPlacement(site);
        double dv = (s.Kind == SiteKind.Main ? EmitterPoiStreamerCore.MainMidHeightMeters : EmitterPoiStreamerCore.RegularMidHeightMeters) - eye;
        double offset = distance > Math.Abs(dv) ? Math.Sqrt(distance * distance - dv * dv) : 0d;
        return new RingCanonicalPosition(s.AroundMeters + offset, s.PadHeightMeters + eye, s.AcrossMeters);
    }

    static double TransformError(Transform3D a, Transform3D b)
    {
        double worst = a.Origin.DistanceTo(b.Origin);
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
                worst = Math.Max(worst, Math.Abs(a.Basis[row][column] - b.Basis[row][column]));
        return worst;
    }

    static void RunPartB(Node3D fixture, EmitterSitePlacementSource placements, List<string> failures)
    {
        int main = placements.Registry.MainIndex;
        int regular = Enumerable.Range(7, 200).First(i => i != main && placements.GetPlacement(i).Kind == SiteKind.Regular);
        int other = Enumerable.Range(0, placements.Count).First(i => i != main && i != regular && placements.GetPlacement(i).Kind == SiteKind.Regular
            && Math.Abs(placements.GetPlacement(i).AroundMeters - placements.GetPlacement(regular).AroundMeters) > 1_000_000d);
        Rig rig = MakeRig(fixture, placements, "RigB");
        EmitterPoiStreamer streamer = rig.Streamer;
        VoxelChunkGridManager grid = rig.Grid;
        EmitterStandInLayer standIns = rig.StandIns;
        streamer.Bind(grid, placements, standIns);
        if (streamer.StartupComplete) failures.Add("startup complete before the pool exists");
        // An anchor change before the pool is ready must be harmless.
        grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(1_000_000d, 100d, 150_000d));

        int built = 0;
        while (!streamer.PoolReady && built++ < 40)
        {
            int before = streamer.SlotCount;
            streamer.AdvanceStartup();
            if (streamer.SlotCount - before > 1) failures.Add("more than one slot instantiated in a single AdvanceStartup call");
        }
        if (!streamer.PoolReady) { failures.Add("pool never became ready"); return; }
        if (streamer.SlotCount != 3 || streamer.FailedKinds.Count != 0) failures.Add($"pool {streamer.SlotCount} failed {streamer.FailedKinds.Count}");
        string[] names = { "RegularSlot0", "RegularSlot1", "MainSlot0" };
        for (int i = 0; i < streamer.SlotCount; i++)
        {
            Node3D? slot = streamer.GetSlotNode(i);
            if (slot == null || slot.Name != names[i] || slot.Visible) failures.Add($"slot {i} name/visibility");
            if (streamer.IsSlotCollisionEnabled(i)) failures.Add($"slot {i} collision enabled at bind");
            if (streamer.GetSlotCollisionNode(i) is CollisionObject3D body)
            {
                Rid space = PhysicsServer3D.BodyGetSpace(body.GetRid());
                if (space.IsValid) failures.Add($"slot {i} body is registered in a physics space while disabled");
            }
            if (streamer.GetSlotFarProxy(i)?.Visible != false) failures.Add($"slot {i} FarProxy visible while stand-ins are bound");
            if (slot?.GetNodeOrNull<Node3D>("PlaceholderBeam")?.Visible != false) failures.Add($"slot {i} placeholder beam visible");
        }
        if (streamer.GetSlotKind(2) != SiteKind.Main || streamer.GetSlotKind(0) != SiteKind.Regular) failures.Add("slot kinds");
        double regularEnd = streamer.DetailVisibleEnd(SiteKind.Regular), mainEnd = streamer.DetailVisibleEnd(SiteKind.Main);
        double regularLoad = streamer.EffectiveLoadRadius(SiteKind.Regular), mainLoad = streamer.EffectiveLoadRadius(SiteKind.Main);
        Console.WriteLine($"EMITTER_POI_STREAMER detail_end regular={regularEnd:0} main={mainEnd:0} effective_load regular={regularLoad:0} main={mainLoad:0}");
        if (regularEnd < 12000d || mainEnd < 28000d) failures.Add($"detail ends {regularEnd}/{mainEnd}");
        if (regularLoad < regularEnd + 500d || mainLoad < mainEnd + 500d || regularLoad < 14000d || mainLoad < 32000d) failures.Add($"effective loads {regularLoad}/{mainLoad}");
        double regularUnload = regularLoad + 2000d;

        // Readiness: needs a completed step.
        if (streamer.StartupComplete) failures.Add("startup complete before the first step");
        StepTo(rig, ObserverAt(placements, regular, regularLoad + 5000d));
        if (!streamer.StartupComplete || streamer.GetLoadingStatus()["error"].AsString().Length != 0 || !streamer.GetLoadingStatus()["ready"].AsBool()) failures.Add("startup not complete after a step");
        if (streamer.Core!.ActiveCount != 0 || standIns.PresentedCount != 0) failures.Add("far observer assigned a site");

        // Approach: presented on the assign step, held by the dwell, cleared after the release.
        StepTo(rig, ObserverAt(placements, regular, regularLoad - 50d));
        int slotIndex = streamer.FindSlotForSite(regular);
        if (slotIndex != 0) { failures.Add($"approach slot {slotIndex}"); return; }
        double assignedAt = streamer.Core.Slots[0].AssignedAt;
        Node3D slotNode = streamer.GetSlotNode(0)!;
        if (!slotNode.Visible || !standIns.IsSitePresented(regular) || standIns.PresentedCount != 1) failures.Add("assign step did not show the slot / mark presented");
        if (streamer.GetSlotFarProxy(0)?.Visible != false) failures.Add("FarProxy visible while stand-ins draw the far silhouette");
        RingCanonicalPosition leaving = ObserverAt(placements, regular, regularUnload + 500d);
        StepTo(rig, leaving, 30);
        if (streamer.FindSlotForSite(regular) != 0 || !standIns.IsSitePresented(regular) || !slotNode.Visible) failures.Add("slot released inside the dwell");
        StepTo(rig, leaving, 29);
        if (streamer.FindSlotForSite(regular) != 0) failures.Add("slot released one frame early");
        StepTo(rig, leaving);
        if (streamer.FindSlotForSite(regular) != -1 || slotNode.Visible || standIns.IsSitePresented(regular) || standIns.PresentedCount != 0 || streamer.IsSlotCollisionEnabled(0))
            failures.Add("release did not hide the slot / clear presented / disable collision");

        // FarProxy shows again when the stand-in layer is disabled.
        standIns.Enabled = false;
        StepTo(rig, leaving);
        if (streamer.GetSlotFarProxy(0)?.Visible != true) failures.Add("FarProxy hidden while the stand-in layer is disabled");
        standIns.Enabled = true;
        StepTo(rig, leaving);
        if (streamer.GetSlotFarProxy(0)?.Visible != false) failures.Add("FarProxy not hidden after stand-ins returned");

        // Collision at P + 1.7 m; held until beyond 2.0 km; the slot stays loaded while farther than that.
        EmitterSitePlacement placement = placements.GetPlacement(regular);
        RingCanonicalPosition atPad = new(placement.AroundMeters, placement.PadHeightMeters + 1.7d, placement.AcrossMeters);
        StepTo(rig, atPad);
        if (streamer.FindSlotForSite(regular) != 0 || !streamer.IsSlotCollisionEnabled(0)) failures.Add("collision not enabled at P + 1.7 m");
        if (streamer.GetSlotCollisionNode(0) is CollisionObject3D active)
            Console.WriteLine($"EMITTER_POI_STREAMER collision_body_space_valid={PhysicsServer3D.BodyGetSpace(active.GetRid()).IsValid}");
        StepTo(rig, new RingCanonicalPosition(placement.AroundMeters + 1800d, placement.PadHeightMeters + 1.7d, placement.AcrossMeters));
        if (!streamer.IsSlotCollisionEnabled(0)) failures.Add("collision dropped at 1.8 km");
        StepTo(rig, new RingCanonicalPosition(placement.AroundMeters + 2100d, placement.PadHeightMeters + 1.7d, placement.AcrossMeters));
        if (streamer.IsSlotCollisionEnabled(0) || streamer.FindSlotForSite(regular) != 0) failures.Add("collision still on at 2.1 km or slot lost");

        // Anchor changes while active, including across the seam: absolute transforms, no drift, dwell not reset.
        StepTo(rig, atPad);
        double dwellStart = streamer.Core.Slots[0].AssignedAt;
        double circumference = grid.GetCanonicalRingCircumferenceMeters64();
        var anchors = new[]
        {
            new RingCanonicalPosition(5_000_000d, 100d, 150_000d),
            new RingCanonicalPosition(circumference - 5d, 100d, 150_000d),
            new RingCanonicalPosition(5d, 200d, 90_000d),
            new RingCanonicalPosition(placement.AroundMeters + 20_000d, 50d, placement.AcrossMeters - 4_000d)
        };
        int refreshes = streamer.AnchorRefreshCount;
        double worst = 0d;
        foreach (RingCanonicalPosition anchor in anchors)
        {
            grid.SetFloatingOriginCanonicalAnchor(anchor);
            RingLodGeometrySnapshot snapshot = grid.CaptureLodGeometrySnapshot();
            Transform3D expected = placements.GetWorldTransform(snapshot, regular);
            worst = Math.Max(worst, TransformError(slotNode.GlobalTransform, expected));
            worst = Math.Max(worst, TransformError(slotNode.GlobalTransform, standIns.GlobalTransform * standIns.GetInstanceTransform(regular)));
            StepTo(rig, atPad);
            if (streamer.FindSlotForSite(regular) != 0 || Math.Abs(streamer.Core.Slots[0].AssignedAt - dwellStart) > 1e-12) failures.Add("anchor change reset or lost the slot");
        }
        if (streamer.AnchorRefreshCount - refreshes != anchors.Length) failures.Add($"anchor refreshes {streamer.AnchorRefreshCount - refreshes}");
        Console.WriteLine($"EMITTER_POI_STREAMER anchor worst_error={worst:E2} refresh_ms_last_assign={streamer.LastAssignMs:F3}");
        if (worst > 0.001d) failures.Add($"slot transform drifted {worst}");

        // Seam site: a site adjacent to the ring seam, observer given unwrapped, anchors on both sides of the seam.
        double c = placements.CircumferenceMeters;
        int seamSite = Enumerable.Range(0, placements.Count).Where(i => placements.GetPlacement(i).Kind == SiteKind.Regular)
            .OrderBy(i => Math.Min(placements.GetPlacement(i).AroundMeters, c - placements.GetPlacement(i).AroundMeters)).First();
        EmitterSitePlacement seam = placements.GetPlacement(seamSite);
        streamer.Enabled = false;
        streamer.Enabled = true;
        var seamObserver = new RingCanonicalPosition(seam.AroundMeters + c + 30d, seam.PadHeightMeters + 1.7d, seam.AcrossMeters);
        StepTo(rig, seamObserver);
        int seamSlot = streamer.FindSlotForSite(seamSite);
        if (seamSlot < 0) failures.Add("seam site not assigned from an unwrapped observer");
        else
        {
            foreach (var anchor in new[] { new RingCanonicalPosition(c - 3d, 0d, seam.AcrossMeters), new RingCanonicalPosition(3d, 0d, seam.AcrossMeters), new RingCanonicalPosition(c - 100d, 0d, seam.AcrossMeters) })
            {
                grid.SetFloatingOriginCanonicalAnchor(anchor);
                double error = TransformError(streamer.GetSlotNode(seamSlot)!.GlobalTransform, placements.GetWorldTransform(grid.CaptureLodGeometrySnapshot(), seamSite));
                if (error > 0.001d) failures.Add($"seam slot transform error {error}");
            }
        }
        grid.ClearFloatingOriginCanonicalAnchor();

        // Teleport to another row: the first slot is held by the dwell, so the spare R1 is used.
        streamer.Enabled = false;
        streamer.Enabled = true;
        StepTo(rig, ObserverAt(placements, regular, 3000d));
        StepTo(rig, ObserverAt(placements, other, 3000d));
        if (streamer.FindSlotForSite(regular) != 0 || streamer.FindSlotForSite(other) != 1) failures.Add($"teleport slots {streamer.FindSlotForSite(regular)}/{streamer.FindSlotForSite(other)}");
        if (standIns.PresentedCount != 2) failures.Add("two active sites not both presented");

        // Main site: presented on the main stand-in, M0 only, unload at its own radius.
        streamer.Enabled = false;
        streamer.Enabled = true;
        if (standIns.PresentedCount != 0) failures.Add("disable left presented flags");
        StepTo(rig, ObserverAt(placements, regular, 31000d));
        if (streamer.Core.ActiveCount != 0) failures.Add("regular site assigned at 31 km");
        StepTo(rig, ObserverAt(placements, main, 31000d));
        if (streamer.FindSlotForSite(main) != 2 || !standIns.IsSitePresented(main) || !streamer.GetSlotNode(2)!.Visible) failures.Add("main site did not take M0 / present the main stand-in");
        StepTo(rig, ObserverAt(placements, main, mainLoad + 2000d + 500d), 61);
        if (streamer.FindSlotForSite(main) != -1 || standIns.IsSitePresented(main)) failures.Add("main slot did not release past its unload radius");

        // Disable.
        StepTo(rig, ObserverAt(placements, regular, 3000d));
        StepTo(rig, ObserverAt(placements, main, 3000d));
        if (standIns.PresentedCount != 2) failures.Add("presented before disable");
        streamer.Enabled = false;
        if (standIns.PresentedCount != 0 || streamer.Core.ActiveCount != 0) failures.Add("disable did not clear presented flags");
        for (int i = 0; i < 3; i++) if (streamer.GetSlotNode(i)!.Visible || streamer.IsSlotCollisionEnabled(i)) failures.Add("disable left a slot visible / colliding");
        StepTo(rig, ObserverAt(placements, regular, 3000d), 10);
        if (streamer.Core.ActiveCount != 0) failures.Add("disabled streamer assigned a slot");
        streamer.Configure(true, 14000, 32000, 2000, 1, 1500, 500);
        StepTo(rig, ObserverAt(placements, regular, 3000d));
        if (streamer.Core.ActiveCount != 1) failures.Add("Configure(enabled) did not resume streaming");
        // Configure clamps to the effective radii.
        streamer.Configure(true, 100, 100, 2000, 1, 1500, 500);
        if (streamer.EffectiveLoadRadius(SiteKind.Regular) < regularEnd + 500d || streamer.EffectiveLoadRadius(SiteKind.Main) < mainEnd + 500d) failures.Add("Configure did not clamp to the effective radii");
        streamer.Configure(true, 14000, 32000, 2000, 1, 1500, 500);

        // Stability: repeated approach/leave cycles leave node and orphan counts unchanged.
        streamer.Enabled = false;
        streamer.Enabled = true;
        double nodes = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount), orphans = Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount);
        double objects = Performance.GetMonitor(Performance.Monitor.ObjectCount);
        for (int cycle = 0; cycle < 12; cycle++)
        {
            StepTo(rig, ObserverAt(placements, cycle % 2 == 0 ? regular : other, 3000d), 5);
            StepTo(rig, ObserverAt(placements, main, 3000d), 5);
            StepTo(rig, ObserverAt(placements, regular, regularUnload + 3000d), 70);
        }
        if (Performance.GetMonitor(Performance.Monitor.ObjectNodeCount) != nodes || Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount) != orphans) failures.Add("node/orphan count changed over cycles");
        if (Performance.GetMonitor(Performance.Monitor.ObjectCount) - objects > Math.Max(64d, objects * 0.005)) failures.Add("object count grew over cycles");

        string status = streamer.DescribeStatus();
        Console.WriteLine("EMITTER_POI_STREAMER status: " + status);
        if (!status.Contains("Pool 3/3") || !status.Contains("R0") || !status.Contains("M0") || !status.Contains("nearest #")) failures.Add("status text: " + status);

        // Exiting the tree clears the presented flags.
        StepTo(rig, ObserverAt(placements, regular, 3000d));
        if (standIns.PresentedCount != 1) failures.Add("presented before exit");
        rig.Root.RemoveChild(streamer);
        if (standIns.PresentedCount != 0) failures.Add("_ExitTree did not clear presented flags");
        streamer.Free();
    }

    static void RunLoadFailures(Node3D fixture, EmitterSitePlacementSource placements, List<string> failures)
    {
        int main = placements.Registry.MainIndex;
        int regular = Enumerable.Range(7, 200).First(i => i != main && placements.GetPlacement(i).Kind == SiteKind.Regular);
        foreach (bool threaded in new[] { false, true })
        {
            string tag = threaded ? "threaded" : "sync";
            Rig rig = MakeRig(fixture, placements, "RigFailMain" + tag, s => { s.MainScenePath = "res://assets/poi/missing/NoSuchMainSpire.tscn"; s.SynchronousLoad = !threaded; });
            EmitterPoiStreamer streamer = rig.Streamer;
            streamer.Bind(rig.Grid, placements, rig.StandIns);
            if (!WarmPool(rig, failures, "main failure " + tag, 4000, threaded)) continue;
            if (streamer.SlotCount != 2 || streamer.FailedKinds.Count != 1 || streamer.FailedKinds[0] != SiteKind.Main) failures.Add($"main failure {tag}: slots {streamer.SlotCount}, failed {streamer.FailedKinds.Count}");
            double regularLoad = streamer.EffectiveLoadRadius(SiteKind.Regular);
            StepTo(rig, ObserverAt(placements, regular, regularLoad - 100d));
            if (!streamer.StartupComplete) failures.Add($"main failure {tag}: startup not complete");
            Godot.Collections.Dictionary status = streamer.GetLoadingStatus();
            if (status["error"].AsString().Length != 0 || !status["ready"].AsBool()) failures.Add($"main failure {tag}: loading status reports an error");
            if (streamer.FindSlotForSite(regular) != 0) failures.Add($"main failure {tag}: regular did not stream");
            StepTo(rig, ObserverAt(placements, main, 3000d));
            if (streamer.FindSlotForSite(main) != -1 || rig.StandIns.IsSitePresented(main)) failures.Add($"main failure {tag}: main stand-in was presented");
            if (!streamer.DescribeStatus().Contains("M0 unavailable")) failures.Add($"main failure {tag}: status text " + streamer.DescribeStatus());
            fixture.RemoveChild(rig.Root);
            rig.Root.Free();
        }

        {
            Rig rig = MakeRig(fixture, placements, "RigFailRegular", s => s.RegularScenePath = "res://assets/poi/missing/NoSuchSpire.tscn");
            EmitterPoiStreamer streamer = rig.Streamer;
            streamer.Bind(rig.Grid, placements, rig.StandIns);
            if (WarmPool(rig, failures, "regular failure"))
            {
                if (streamer.SlotCount != 1 || streamer.GetSlotKind(0) != SiteKind.Main || streamer.FailedKinds.Count != 1 || streamer.FailedKinds[0] != SiteKind.Regular) failures.Add("regular failure pool");
                StepTo(rig, ObserverAt(placements, regular, 3000d));
                StepTo(rig, ObserverAt(placements, main, 3000d));
                if (streamer.FindSlotForSite(regular) != -1 || streamer.FindSlotForSite(main) != 0 || !rig.StandIns.IsSitePresented(main) || rig.StandIns.IsSitePresented(regular)) failures.Add("regular failure behavior");
                if (!streamer.StartupComplete || streamer.GetLoadingStatus()["error"].AsString().Length != 0) failures.Add("regular failure blocked loading");
            }
            fixture.RemoveChild(rig.Root);
            rig.Root.Free();
        }

        {
            Rig rig = MakeRig(fixture, placements, "RigFailBoth", s => { s.RegularScenePath = "res://x/a.tscn"; s.MainScenePath = "res://x/b.tscn"; });
            rig.Streamer.Bind(rig.Grid, placements, rig.StandIns);
            if (WarmPool(rig, failures, "both failure"))
            {
                StepTo(rig, ObserverAt(placements, regular, 3000d));
                if (rig.Streamer.SlotCount != 0 || !rig.Streamer.StartupComplete || rig.Streamer.FailedKinds.Count != 2) failures.Add("both failure pool / readiness");
            }
            fixture.RemoveChild(rig.Root);
            rig.Root.Free();
        }
    }
}
