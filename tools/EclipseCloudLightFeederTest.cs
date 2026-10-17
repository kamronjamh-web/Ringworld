using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Underground;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Tweaks T4/T5 (docs/eclipse-false-sun-spec.md): the cloud light feeder. Selection (beams and orbs before grid links, nearest first, at most the setting), the
/// segment geometry (the in-deck column of the beam, the orb capsule, the pulse window of a grid link), the budgets (a bounded evaluation rate, pushes only when the
/// quantised set changes, no allocation in a steady frame) and the gates (nothing before the fire, with the feature off, with the grid links off, underground, after
/// the cut). The sink is a test double; the Sunshine shader itself is covered by the captures and the zero-segment parity run.
/// </summary>
public partial class EclipseCloudLightFeederTest : RefCounted
{
    sealed class FakeSink : ISunshineSegmentLightSink
    {
        public bool Available = true;
        public int Pushes;
        public SunshineSegmentLight[] Held = Array.Empty<SunshineSegmentLight>();
        public bool SegmentLightsAvailable => Available;
        public bool TryGetCloudLightBand(out float floorMeters, out float ceilingMeters, out float reachMeters) { floorMeters = 2000f; ceilingMeters = 6000f; reachMeters = 80_000f; return true; }
        public bool SetSegmentLights(ReadOnlySpan<SunshineSegmentLight> lights)
        {
            if (lights.SequenceEqual(Held)) return false;
            Held = lights.ToArray();
            Pushes++;
            return true;
        }
    }

    static void Expect(List<string> failures, bool ok, string message) { if (!ok) failures.Add(message); }

    static readonly Vector3 Teal = EclipseCloudLightFeeder.BeamColor;
    static SunshineSegmentLight Seg(float x, bool steady = true, float intensity = 1f) => new(new Vector3(x, 0f, 0f), new Vector3(x, 1000f, 0f), 3000f, Teal * intensity, steady);

    public int Run(bool unused)
    {
        _ = unused;
        var failures = new List<string>();
        Node3D? fixture = null;
        try
        {
            SceneTree tree = (Engine.GetMainLoop() as SceneTree) ?? throw new InvalidOperationException("scene tree unavailable");
            CheckFeeder(failures);

            using RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen17(1337);
            using INaturalHeightSource source = EmitterSiteHeightSources.Create(sampler);
            EmitterSiteRegistry registry = EmitterSiteRegistry.GetOrBuild(EmitterSiteRecipe.Default(1337), source);
            EmitterGridTopology topology = EmitterGridTopology.Build(registry);
            EclipseSiteTable table = EclipseSiteTable.FromTopology(topology);
            var clock = new EmitterGridPulseClock(1337, topology.CircumferenceMeters);
            var window = new EclipseEventWindow(0, 12_048.45d, 16_138.59d);
            var timeline = new EclipseEventTimeline(new EclipseEventSettings(), window, table, clock, 1337);

            fixture = new Node3D { Name = "EclipseCloudLightFixture" };
            tree.Root.AddChild(fixture);
            var grid = new VoxelChunkGridManager
            {
                Name = "ChunkGridManager",
                BuildOnReady = false,
                DormantEditableBackend = true,
                EnableChunkStreaming = false,
                UseDeterministicTerrainGeneration = true,
                EnableCanonicalFloatingOriginAnchor = true,
                TerrainSettings = new VoxelTerrainSettings()
            };
            fixture.AddChild(grid);
            grid.ConfigureRuntimeRingTopology(EmitterSiteTestWorlds.Circumference / 32, EmitterSiteTestWorlds.Circumference / Math.Tau);
            var camera = new Camera3D { Name = "Camera", Current = true, Far = 1_200_000f };
            fixture.AddChild(camera);
            var gridLayer = new EmitterGridBeamLayer { Name = "EmitterGrid", AutoBind = false, LoadSavedDefaults = false };
            fixture.AddChild(gridLayer);
            if (!gridLayer.Bind(grid, topology)) { Console.Error.WriteLine("ECLIPSE_CLOUD_LIGHT=FAIL grid bind"); return 1; }
            var layer = new EclipseHubBeamLayer { Name = "EclipseBeams", AutoBind = false };
            fixture.AddChild(layer);
            if (!layer.Bind(grid, topology)) { Console.Error.WriteLine("ECLIPSE_CLOUD_LIGHT=FAIL beam bind"); return 1; }
            layer.ResolveCollaborators();
            var sink = new FakeSink();
            layer.CloudLightSinkOverride = sink;
            layer.CloudLightBandOverride = new Vector3(2000f, 6000f, 80_000f);
            layer.RefuelFrontClock = clock;
            layer.BindTimeline(timeline);

            CheckBeams(layer, timeline, topology, camera, sink, failures);
            CheckOrbs(layer, timeline, topology, camera, sink, clock, failures);
            CheckGrid(gridLayer, topology, clock, failures);
            CheckGates(layer, gridLayer, timeline, camera, sink, failures);
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
        if (failures.Count != 0) { Console.Error.WriteLine("ECLIPSE_CLOUD_LIGHT=FAIL " + string.Join(" | ", failures.Take(25))); return 1; }
        Console.WriteLine("ECLIPSE_CLOUD_LIGHT=PASS");
        return 0;
    }

    // ---------------------------------------------------------------- the feeder alone

    static void CheckFeeder(List<string> failures)
    {
        // Selection: tier first, then distance, equal keys keep the earlier offer; the limit drops the worst.
        var rng = new Random(5);
        var feeder = new EclipseCloudLightFeeder();
        var sink = new FakeSink();
        for (int round = 0; round < 50; round++)
        {
            int limit = 1 + rng.Next(8);
            feeder.Begin(limit);
            var offered = new List<(int Tier, double Distance, int Id)>();
            int n = 1 + rng.Next(20);
            for (int i = 0; i < n; i++)
            {
                int tier = rng.Next(2);
                double distance = Math.Round(rng.NextDouble() * 10d) * 100d; // many ties
                offered.Add((tier, distance, i));
                feeder.Offer(Seg(i), tier, distance);
            }
            int[] expected = offered.OrderBy(o => o.Tier).ThenBy(o => o.Distance).ThenBy(o => o.Id).Take(limit).Select(o => o.Id).ToArray();
            var local = new FakeSink();
            feeder.ForgetPushed();
            feeder.Commit(local);
            int[] got = local.Held.Select(s => (int)s.A.X).ToArray();
            if (!got.SequenceEqual(expected)) { failures.Add($"selection round {round}: got [{string.Join(",", got)}] expected [{string.Join(",", expected)}]"); break; }
        }

        // Pushes only on change, quantised: sub-metre jitter is no change; a clear is one push and idempotent; an unavailable sink is left alone.
        feeder = new EclipseCloudLightFeeder();
        feeder.Begin(4);
        feeder.Offer(Seg(10f), 0, 5d);
        Expect(failures, feeder.Commit(sink) && sink.Pushes == 1 && feeder.PushCount == 1, "first commit did not push");
        feeder.Begin(4);
        feeder.Offer(new SunshineSegmentLight(new Vector3(10.3f, 0.2f, -0.3f), new Vector3(10.2f, 1000.4f, 0f), 3000.3f, Teal * 1.0004f, true), 0, 5d);
        Expect(failures, !feeder.Commit(sink) && sink.Pushes == 1 && feeder.PushCount == 1, "sub-metre jitter pushed");
        feeder.Begin(4);
        feeder.Offer(Seg(12f), 0, 5d);
        Expect(failures, feeder.Commit(sink) && sink.Pushes == 2, "a moved segment did not push");
        feeder.Begin(4);
        feeder.Offer(Seg(12f, steady: false), 0, 5d);
        Expect(failures, feeder.Commit(sink) && sink.Pushes == 3 && !sink.Held[0].Steady, "the steady flag change did not push");
        Expect(failures, feeder.Clear(sink) && sink.Pushes == 4 && sink.Held.Length == 0 && !feeder.Clear(sink) && sink.Pushes == 4 && feeder.PushedCount == 0, "clear is not one push and idempotent");
        sink.Available = false;
        feeder.Begin(4);
        feeder.Offer(Seg(1f), 0, 1d);
        Expect(failures, !feeder.Commit(sink) && sink.Pushes == 4 && feeder.PushedCount == 0, "an unavailable sink was written");
        sink.Available = true;
        Expect(failures, feeder.Commit(sink) && sink.Pushes == 5, "the held set was not pushed once the sink came back");
        // Quantize is idempotent and keeps the flag.
        SunshineSegmentLight q = EclipseCloudLightFeeder.Quantize(new SunshineSegmentLight(new Vector3(1.4f, 2.6f, -3.5f), new Vector3(0.49f, 0f, 9f), 2999.6f, new Vector3(0.123456f, -1f, 2.5f), false));
        Expect(failures, EclipseCloudLightFeeder.Quantize(q) == q && !q.Steady && q.Radius == 3000f && q.Color.Y == 0f && q.A == new Vector3(1f, 3f, -4f), $"quantize {q}");

        // The evaluation clock: 30 Hz from 60 fps frames, and a long frame is one evaluation, not a burst.
        feeder = new EclipseCloudLightFeeder();
        int due = 0;
        for (int i = 0; i < 120; i++) if (feeder.BeginIfDue(1d / 60d, 6)) due++;
        Expect(failures, due is >= 59 and <= 61, $"evaluations per 2 s at 60 fps: {due}");
        due = 0;
        for (int i = 0; i < 5; i++) if (feeder.BeginIfDue(1.0d, 6)) due++;
        Expect(failures, due == 5, $"long frames: {due} evaluations in 5 frames");
        Console.WriteLine("ECLIPSE_CLOUD_LIGHT feeder selection ok, pushes only on quantised change, evaluation clock 30 Hz");
    }

    // ---------------------------------------------------------------- hub beams

    static (int Beam, Vector3D Origin) FocusBeam(EclipseHubBeamLayer layer, int beam, Camera3D camera)
    {
        Vector3D o = layer.GetWorldOrigin(beam);
        camera.GlobalPosition = new Vector3((float)o.X, (float)o.Y + 50f, (float)o.Z);
        return (beam, o);
    }

    static double Dist(Vector3 p, Vector3 a, Vector3 b) => EclipseCloudLightFeeder.DistanceToSegment(new Vector3D(p.X, p.Y, p.Z), new Vector3D(a.X, a.Y, a.Z), new Vector3D(b.X, b.Y, b.Z));

    static void Frames(EclipseHubBeamLayer layer, double tau, int frames, double? gridTime = null, double delta = 1d / 60d)
    {
        for (int i = 0; i < frames; i++) layer.ApplyEventTime(tau, true, delta, gridTime);
    }

    static void CheckBeams(EclipseHubBeamLayer layer, EclipseEventTimeline timeline, EmitterGridTopology topology, Camera3D camera, FakeSink sink, List<string> failures)
    {
        layer.ApplyBeamSettings(new EclipseBeamSettings { CloudLightGridLinks = false });
        EclipseEventTimeline eclipse = layer.BoundTimeline!;
        double tau = eclipse.TauHandoff - 2d;
        int focus = 60;
        FocusBeam(layer, focus, camera);

        // Before any beam fires nothing is offered and nothing is pushed.
        Frames(layer, eclipse.TauMainFire - 1d, 10);
        Expect(failures, sink.Pushes == 0 && sink.Held.Length == 0 && layer.CloudLight.PushCount == 0, $"cloud light before the fire: {sink.Pushes} pushes");

        // The lit beams near the camera: one segment each, the focus beam first, nearest first, in the deck column along the axis.
        Frames(layer, tau, 12);
        SunshineSegmentLight[] held = sink.Held;
        Expect(failures, held.Length >= 1 && held.Length <= new EclipseBeamSettings().CloudLightMaxSegments, $"segments {held.Length}");
        if (held.Length == 0) return;
        Expect(failures, held.All(s => s.Steady), "a beam segment is not steady (history path)");
        double previous = -1d;
        foreach (SunshineSegmentLight s in held)
        {
            double d = Dist(camera.GlobalPosition, s.A, s.B);
            if (d < previous - 1.5d) failures.Add($"segments not nearest first ({d:F1} after {previous:F1})");
            previous = d;
        }
        SunshineSegmentLight first = held[0];
        Vector3D origin = layer.GetWorldOrigin(focus), hub = layer.HubWorld;
        Vector3D axis = (hub - origin).Normalized();
        double altitude = topology.HubBeamOrigin(focus, layer.Beams.BeamOrigin).Vertical;
        // Both endpoints lie on the beam axis (within the 1 m quantisation) and the column spans the deck (floor - 500 .. ceiling + 500 m) along the axis.
        foreach (Vector3 p in new[] { first.A, first.B })
        {
            var rel = new Vector3D(p.X - origin.X, p.Y - origin.Y, p.Z - origin.Z);
            double along = rel.Dot(axis);
            var perp = new Vector3D(rel.X - axis.X * along, rel.Y - axis.Y * along, rel.Z - axis.Z * along);
            Expect(failures, perp.Length < 2d, $"endpoint {p} is {perp.Length:F2} m off the beam axis");
        }
        double length = (first.B - first.A).Length();
        Expect(failures, altitude < 1500d && Math.Abs(length - 5000d) < 12d, $"column length {length:F1} m (origin altitude {altitude:F0})");
        Expect(failures, first.Radius == 4000f, $"radius {first.Radius}");
        // The colour is the beam colour times intensity times its level (1 here): quantised to 1/256.
        Expect(failures, Math.Abs(first.Color.X - Teal.X * 5f) < 0.02f && Math.Abs(first.Color.Y - Teal.Y * 5f) < 0.02f && Math.Abs(first.Color.Z - Teal.Z * 5f) < 0.02f, $"colour {first.Color}");
        Console.WriteLine($"ECLIPSE_CLOUD_LIGHT beams segments={held.Length} first_len={length:F1} first_rgb=({first.Color.X:F2},{first.Color.Y:F2},{first.Color.Z:F2}) pushes={sink.Pushes}");

        // A steady event time pushes nothing more: 600 frames at 60 fps (300 evaluations) leave the push counts alone, and the evaluations did run.
        int pushes = sink.Pushes; long feederPushes = layer.CloudLight.PushCount, evaluations = layer.CloudLight.EvaluationCount;
        Frames(layer, tau, 600);
        Expect(failures, sink.Pushes == pushes && layer.CloudLight.PushCount == feederPushes, $"steady frames pushed ({sink.Pushes - pushes})");
        Expect(failures, layer.CloudLight.EvaluationCount - evaluations is >= 290 and <= 310, $"evaluations in 10 s: {layer.CloudLight.EvaluationCount - evaluations}");

        // No allocation in a steady frame: the same 600 frames with the feature off against on (after a warm-up) differ by a few hundred bytes at most.
        Frames(layer, tau, 60);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        Frames(layer, tau, 600);
        long withLight = GC.GetAllocatedBytesForCurrentThread() - a0;
        layer.ApplyBeamSettings(layer.Beams with { CloudLightEnabled = false });
        Frames(layer, tau, 60);
        long a1 = GC.GetAllocatedBytesForCurrentThread();
        Frames(layer, tau, 600);
        long without = GC.GetAllocatedBytesForCurrentThread() - a1;
        Console.WriteLine($"ECLIPSE_CLOUD_LIGHT allocation 600 steady frames: cloud light on {withLight} B, off {without} B");
        Expect(failures, withLight - without <= 2048, $"the cloud light allocates in steady frames ({withLight - without} B more over 600 frames)");
        Expect(failures, sink.Held.Length == 0, "turning the feature off did not clear the segments");
        layer.ApplyBeamSettings(layer.Beams with { CloudLightEnabled = true });

        // The setting caps the count; nearest first holds for every cap; the radius and intensity settings reach the segment.
        EclipseBeamSettings beams = layer.Beams;
        Vector3 farFocus = camera.GlobalPosition;
        // Stand between several beams so more than one is in reach: mid-point of the two nearest beams' origins, with a generous reach.
        layer.CloudLightBandOverride = new Vector3(2000f, 6000f, 400_000f);
        int[] counts = new int[9];
        for (int cap = 1; cap <= 8; cap++)
        {
            layer.ApplyBeamSettings(beams with { CloudLightMaxSegments = cap });
            Frames(layer, tau, 12);
            counts[cap] = sink.Held.Length;
            Expect(failures, counts[cap] == cap, $"cap {cap}: {counts[cap]} segments (400 km reach holds more than 8 beams)");
            double last = -1d;
            foreach (SunshineSegmentLight s in sink.Held)
            {
                double d = Dist(camera.GlobalPosition, s.A, s.B);
                if (d < last - 1.5d) failures.Add($"cap {cap}: not nearest first");
                last = d;
            }
        }
        layer.ApplyBeamSettings(beams with { CloudLightMaxSegments = 99 }); // sanitised to 8
        Frames(layer, tau, 12);
        Expect(failures, layer.Beams.CloudLightMaxSegments == 8 && sink.Held.Length == 8, $"cap 99 -> {layer.Beams.CloudLightMaxSegments} / {sink.Held.Length}");
        layer.ApplyBeamSettings(beams with { CloudLightRadiusMeters = 2500d, CloudLightIntensity = 2d });
        Frames(layer, tau, 12);
        Expect(failures, sink.Held.Length > 0 && sink.Held[0].Radius == 2500f && Math.Abs(sink.Held[0].Color.X - Teal.X * 2f) < 0.02f, $"radius/intensity settings: {(sink.Held.Length > 0 ? sink.Held[0].Radius : -1)}");
        layer.CloudLightBandOverride = new Vector3(2000f, 6000f, 80_000f);
        layer.ApplyBeamSettings(new EclipseBeamSettings { CloudLightGridLinks = false });
        camera.GlobalPosition = farFocus;

        // The reach: a camera far from every beam gets nothing; the set it had is cleared with one push.
        Frames(layer, tau, 12);
        Expect(failures, sink.Held.Length >= 1, "no segments back at the focus beam");
        pushes = sink.Pushes;
        camera.GlobalPosition = new Vector3(0f, 900_000f, 0f);
        Frames(layer, tau, 12);
        Expect(failures, sink.Held.Length == 0 && sink.Pushes == pushes + 1, $"a camera out of reach: {sink.Held.Length} segments, {sink.Pushes - pushes} pushes");
        FocusBeam(layer, focus, camera);
        Frames(layer, tau, 12);

        // A climbing tip clips the column: 500 m up the beam is below the deck (no segment for it), 3,000 m up the column ends there.
        double fire = eclipse.SiteFireTau(focus - 1), lengthM = (layer.HubWorld - origin).Length;
        Frames(layer, fire + 0.0002d, 12);
        Expect(failures, !sink.Held.Any(h => OffAxis(h.A, origin, axis) < 3d), "a beam below the deck produced a segment");
        Frames(layer, fire + 1.5d * 3000d / lengthM, 12);
        SunshineSegmentLight? climbing = sink.Held.Cast<SunshineSegmentLight?>().FirstOrDefault(h => OffAxis(h!.Value.A, origin, axis) < 3d && OffAxis(h.Value.B, origin, axis) < 3d);
        Expect(failures, climbing != null && Math.Abs(AlongAxis(climbing.Value.B, origin, axis) - 3000d) < 5d, $"the climbing column ends {(climbing == null ? -1d : AlongAxis(climbing.Value.B, origin, axis)):F1} m up (expected 3000)");
    }

    static double AlongAxis(Vector3 p, Vector3D origin, Vector3D axis) => new Vector3D(p.X - origin.X, p.Y - origin.Y, p.Z - origin.Z).Dot(axis);

    static double OffAxis(Vector3 p, Vector3D origin, Vector3D axis)
    {
        var rel = new Vector3D(p.X - origin.X, p.Y - origin.Y, p.Z - origin.Z);
        double along = rel.Dot(axis);
        return new Vector3D(rel.X - axis.X * along, rel.Y - axis.Y * along, rel.Z - axis.Z * along).Length;
    }

    // ---------------------------------------------------------------- orbs

    static void CheckOrbs(EclipseHubBeamLayer layer, EclipseEventTimeline timeline, EmitterGridTopology topology, Camera3D camera, FakeSink sink, EmitterGridPulseClock clock, List<string> failures)
    {
        layer.ApplyBeamSettings(new EclipseBeamSettings { CloudLightGridLinks = false });
        layer.CloudLightBandOverride = new Vector3(2000f, 6000f, 80_000f);
        double s = timeline.Window.Start;
        int entry = 9, beam = entry + 1;
        long n = timeline.RefuelLaunchAtOrAfter(entry, s + timeline.TauSustain + 300d);
        double pass = timeline.RefuelArrivalTime(entry, n);
        FocusBeam(layer, beam, camera);
        layer.RefuelFrontClock = clock;
        // 5.4 s after the launch the orb is about 6 km up the beam (age^3), inside the deck column; the beam itself is dark after the hand-off.
        double age = 3.0d, gridTime = pass + age, tau = gridTime - s;
        Frames(layer, tau, 12, gridTime);
        SunshineSegmentLight[] held = sink.Held;
        var (envelope, fraction) = layer.GetOrb(beam);
        Console.WriteLine($"ECLIPSE_CLOUD_LIGHT orbs age={age:F1}s envelope={envelope:F3} fraction={fraction:E3} segments={held.Length}");
        Expect(failures, envelope > 0.99f, $"orb envelope {envelope}");
        SunshineSegmentLight? orb = held.Cast<SunshineSegmentLight?>().FirstOrDefault(h => h!.Value.Steady == false);
        Expect(failures, orb != null, "no transient segment for the orb in the deck");
        if (orb is { } o)
        {
            Vector3D origin = layer.GetWorldOrigin(beam), hub = layer.HubWorld;
            Vector3D axis = (hub - origin).Normalized();
            double length = (hub - origin).Length, expected = fraction * length;
            Vector3 centre = (o.A + o.B) * 0.5f;
            var rel = new Vector3D(centre.X - origin.X, centre.Y - origin.Y, centre.Z - origin.Z);
            double along = rel.Dot(axis);
            Expect(failures, Math.Abs(along - expected) < 3d, $"orb centre {along:F1} m along the beam, expected {expected:F1}");
            Expect(failures, Math.Abs((o.B - o.A).Length() - 700d) < 3d, $"orb capsule length {(o.B - o.A).Length():F1} m (2 x the orb radius)");
            Expect(failures, o.Radius == 3000f, $"orb radius {o.Radius} (0.75 x the cloud light radius)");
            Expect(failures, Math.Abs(o.Color.X - Teal.X * 5f * EclipseHubBeamLayer.OrbCloudLightGain) < 0.05f, $"orb colour {o.Color}");
        }
        Expect(failures, held.All(h => !h.Steady), "a dark (handed-off) beam still has a steady segment");

        // Before the launch and below the deck (0.5 s after it the orb is 23 m up) this beam's orb is not offered. (Neighbouring spires launch within seconds of it, so their
        // own orbs may be in the deck: only segments on this beam's axis count.)
        Vector3D focusOrigin = layer.GetWorldOrigin(beam), focusAxis = (layer.HubWorld - focusOrigin).Normalized();
        int basePushes = sink.Pushes;
        Frames(layer, pass - 1d - s, 12, pass - 1d);
        Expect(failures, !sink.Held.Any(h => OffAxis(h.A, focusOrigin, focusAxis) < 3d), "orb light before the launch");
        Frames(layer, pass + 0.5d - s, 12, pass + 0.5d);
        Expect(failures, !sink.Held.Any(h => OffAxis(h.A, focusOrigin, focusAxis) < 3d), "orb light below the deck");
        // Moving orb: consecutive evaluations differ, so each pushes (bounded by the 30 Hz evaluation rate).
        basePushes = sink.Pushes;
        for (int i = 0; i < 30; i++) layer.ApplyEventTime(pass + 2.6d + i / 60d - s, true, 1d / 60d, pass + 2.6d + i / 60d);
        int moving = sink.Pushes - basePushes;
        Console.WriteLine($"ECLIPSE_CLOUD_LIGHT orbs moving pushes in 30 frames (0.5 s) = {moving}");
        Expect(failures, moving is >= 8 and <= 16, $"a moving orb pushed {moving} times in 0.5 s (30 Hz evaluations)");
    }

    // ---------------------------------------------------------------- grid links

    static void CheckGrid(EmitterGridBeamLayer gridLayer, EmitterGridTopology topology, EmitterGridPulseClock clock, List<string> failures)
    {
        // A pulse head in the middle of a chain link: the segment is the chord within the radius of the camera's closest point, coloured by the pulse there.
        int column = 1, k = 50;
        EmitterLink link = topology[topology.ChainFirstLink(column) + k];
        Vector3D a = gridLayer.GetWorldEndpointA(link.Index), b = gridLayer.GetWorldEndpointB(link.Index);
        double chord = (b - a).Length;
        double pMid = 0.5d * (link.P0 + link.P1), q = clock.ToQ(pMid);
        double t = clock.ArrivalTime(q, column, 40) + 0.2d; // the head is 21 km past the middle of the link
        gridLayer.UpdateFronts(t);
        Vector3D mid = (a + b) * 0.5d;
        double altitudeMid = topology.RelayNode(link.FromSite).Vertical * 0.5d + topology.RelayNode(link.ToSite).Vertical * 0.5d + topology.GetGeometry(link.Index).SagittaM;
        var camera = new Vector3D(mid.X + 800d, mid.Y, mid.Z);
        var feeder = new EclipseCloudLightFeeder();
        var sink = new FakeSink();
        double floor = altitudeMid - 1500d, ceiling = altitudeMid + 1500d;
        feeder.Begin(6);
        gridLayer.GatherCloudLight(feeder, camera, floor, ceiling, 50_000d, 4000f, 2.5f, Teal, t);
        feeder.Commit(sink);
        Expect(failures, sink.Held.Length >= 1, $"a pulse head mid-link offered no grid segment (mid altitude {altitudeMid:F0})");
        if (sink.Held.Length >= 1)
        {
            SunshineSegmentLight s = sink.Held[0];
            Expect(failures, !s.Steady && s.Radius == 4000f, $"grid segment flags {s.Steady} radius {s.Radius}");
            double d = EclipseCloudLightFeeder.DistanceToSegment(camera, new Vector3D(s.A.X, s.A.Y, s.A.Z), new Vector3D(s.B.X, s.B.Y, s.B.Z));
            double length = (s.B - s.A).Length();
            double expectedLength = Math.Min(chord, 8000d);
            Expect(failures, d < 900d && Math.Abs(length - expectedLength) < 5d, $"grid segment {length:F0} m long, {d:F0} m from the camera (chord {chord:F0})");
            double pulse = clock.PulseAtLink(t, link, 0.5d);
            double got = s.Color.X / (Teal.X * 2.5f);
            Expect(failures, Math.Abs(got - pulse) < 0.03d, $"grid pulse brightness {got:F3} against the clock {pulse:F3}");
            // The endpoints lie on the link's chord.
            foreach (Vector3 p in new[] { s.A, s.B })
            {
                var ap = new Vector3D(p.X - a.X, p.Y - a.Y, p.Z - a.Z);
                Vector3D dir = (b - a).Normalized();
                double along = ap.Dot(dir);
                var perp = new Vector3D(ap.X - dir.X * along, ap.Y - dir.Y * along, ap.Z - dir.Z * along);
                Expect(failures, perp.Length < 2d, $"grid endpoint {perp.Length:F1} m off the chord");
            }
            Console.WriteLine($"ECLIPSE_CLOUD_LIGHT grid link={link.Index} segment_m={length:F0} pulse={got:F3} (clock {pulse:F3}) altitude_mid={altitudeMid:F0}");
        }

        // A deck the link does not cross, a dark grid (no front on the column yet), a hidden layer and a suppressed normal pulse offer nothing.
        feeder.Begin(6);
        gridLayer.GatherCloudLight(feeder, camera, 60_000d, 70_000d, 50_000d, 4000f, 2.5f, Teal, t);
        Expect(failures, feeder.CandidateCount == 0, "a deck the link does not cross lit the grid");
        double early = clock.LaunchTime(0, column) - 5d;
        gridLayer.UpdateFronts(early);
        feeder.Begin(6);
        gridLayer.GatherCloudLight(feeder, camera, floor, ceiling, 50_000d, 4000f, 2.5f, Teal, early);
        Expect(failures, feeder.CandidateCount == 0, $"a dark grid offered {feeder.CandidateCount} segments");
        gridLayer.UpdateFronts(t);
        gridLayer.NormalPulseWeight = 0f;
        gridLayer.UpdateFronts(t);
        feeder.Begin(6);
        gridLayer.GatherCloudLight(feeder, camera, floor, ceiling, 50_000d, 4000f, 2.5f, Teal, t);
        Expect(failures, feeder.CandidateCount == 0, "a suppressed normal pulse lit the clouds");
        // An ignition front's plateau lights the link while the normal pulse is suppressed.
        EmitterEventFront front = clock.MakeEventFront(column, pMid + 30_000d, 100_000d, towardIncreasingP: true, plateau: 0.35d, headless: false);
        gridLayer.SetEventFronts(new[] { new Vector4((float)front.HeadQ, front.Column, (float)front.SignedTravelled, (float)front.Plateau) }, 1f);
        feeder.Begin(6);
        gridLayer.GatherCloudLight(feeder, camera, floor, ceiling, 50_000d, 4000f, 2.5f, Teal, t);
        Expect(failures, feeder.CandidateCount >= 1, "an ignition plateau did not light the clouds");
        gridLayer.SetEventFronts(ReadOnlySpan<Vector4>.Empty, 0f);
        gridLayer.NormalPulseWeight = 1f;
        gridLayer.UpdateFronts(t);
        long gathers = gridLayer.CloudLightGatherCount;
        gridLayer.SetUndergroundHidden(true);
        feeder.Begin(6);
        gridLayer.GatherCloudLight(feeder, camera, floor, ceiling, 50_000d, 4000f, 2.5f, Teal, t);
        Expect(failures, feeder.CandidateCount == 0 && gridLayer.CloudLightGatherCount == gathers + 1, "an underground (hidden) grid layer lit the clouds");
        gridLayer.SetUndergroundHidden(false);
    }

    // ---------------------------------------------------------------- the gates

    static void CheckGates(EclipseHubBeamLayer layer, EmitterGridBeamLayer gridLayer, EclipseEventTimeline timeline, Camera3D camera, FakeSink sink, List<string> failures)
    {
        layer.ApplyBeamSettings(new EclipseBeamSettings { CloudLightGridLinks = false });
        layer.CloudLightBandOverride = new Vector3(2000f, 6000f, 80_000f);
        EclipseEventTimeline eclipse = layer.BoundTimeline!;
        double tau = eclipse.TauHandoff - 2d;
        FocusBeam(layer, 60, camera);
        Frames(layer, tau, 12);
        Expect(failures, sink.Held.Length >= 1, "no segments to gate");

        // Grid links off: the grid layer is never asked; on: it is asked at the evaluation rate.
        long gathers = gridLayer.CloudLightGatherCount;
        Frames(layer, tau, 60);
        Expect(failures, gridLayer.CloudLightGatherCount == gathers, "grid links off but the grid was gathered");
        layer.ApplyBeamSettings(layer.Beams with { CloudLightGridLinks = true });
        gathers = gridLayer.CloudLightGatherCount;
        Frames(layer, tau, 60);
        Expect(failures, gridLayer.CloudLightGatherCount - gathers is >= 28 and <= 32, $"grid links on: {gridLayer.CloudLightGatherCount - gathers} gathers in 60 frames");
        layer.ApplyBeamSettings(layer.Beams with { CloudLightGridLinks = false });

        // Underground: hold and hidden both clear the segments with one push and then write nothing.
        int pushes = sink.Pushes;
        layer.UndergroundHold = true;
        Frames(layer, tau, 12);
        Expect(failures, sink.Held.Length == 0 && sink.Pushes == pushes + 1, $"underground hold: {sink.Held.Length} segments, {sink.Pushes - pushes} pushes");
        Frames(layer, tau, 120);
        Expect(failures, sink.Pushes == pushes + 1, "underground wrote again");
        layer.UndergroundHold = false;
        Frames(layer, tau, 12);
        Expect(failures, sink.Held.Length >= 1, "the segments did not return above ground");
        pushes = sink.Pushes;
        layer.SetUndergroundHidden(true);
        Frames(layer, tau, 12);
        Expect(failures, sink.Held.Length == 0 && sink.Pushes == pushes + 1, "underground hidden did not clear");
        layer.SetUndergroundHidden(false);
        Frames(layer, tau, 12);

        // The beams hidden (not lit) clear; an unavailable sink gets nothing; the cut and the unbind clear.
        pushes = sink.Pushes;
        layer.ApplyEventTime(tau, false, 0.1d);
        Expect(failures, sink.Held.Length == 0 && sink.Pushes == pushes + 1, "an unlit layer kept the segments");
        Frames(layer, tau, 12);
        Expect(failures, sink.Held.Length >= 1, "the segments did not return when the beams lit again");
        double cut = eclipse.TauImplosion + eclipse.BeamRetract + 0.5d;
        Frames(layer, cut, 12);
        Expect(failures, sink.Held.Length == 0, $"segments after the cut: {sink.Held.Length}");
        Frames(layer, tau, 12);
        layer.Unbind();
        Expect(failures, sink.Held.Length == 0 && layer.CloudLight.PushedCount == 0, "unbind did not clear the segments");
        // Gate summary for the log.
        Console.WriteLine($"ECLIPSE_CLOUD_LIGHT gates feeder_pushes={layer.CloudLight.PushCount} sink_pushes={sink.Pushes}");
    }
}
