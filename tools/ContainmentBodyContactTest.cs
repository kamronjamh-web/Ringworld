using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Containment field contact ripples and WO10 audio (docs/containment-field-design.md decisions 4 and 8): the body-contact adapter (crossing detection in both
/// directions across the arch and the skirt on the wall top, no false crossings walking along the wall top beside the root, the per-body cooldown, deterministic
/// SourceIds, registered bodies, teleport rejection, radius/energy scaling) and the vacuum-side low-pass and gain as functions of signed distance, plus the
/// bus effects on a real audio bus. Pure C# plus the audio server; no scene.
/// Run: Godot --headless --path . --script res://tools/containment_body_contact_test.gd. Prints CONTAINMENT_BODY_CONTACT=PASS.
/// </summary>
public partial class ContainmentBodyContactTest : RefCounted
{
    private const double W = 318_000.0, Dt = 1.0 / 60.0;
    private static readonly double Circ = CertifiedRingScale.EffectiveCircumferenceMeters;

    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok) _failures.Add(message); }

    private static RingLodGeometrySnapshot Snap(double wallTop, RingCanonicalPosition anchor)
        => new(new Transform3D(new Basis(Vector3.Up, 0.3f), new Vector3(5f, -7f, 9f)), Vector3.Zero, Vector3.Zero, CertifiedRingScale.EffectiveRadiusMeters, 0.0, 0.0,
            Circ, -8192.0, wallTop, anchor);

    private sealed class ListSink : IContainmentDisturbanceSink
    {
        public readonly List<ContainmentDisturbance> Published = new();
        public bool Publish(in ContainmentDisturbance d) { Published.Add(d); return true; }
    }

    private sealed class Body : IContainmentBody
    {
        public long BodyId { get; set; } = 7;
        public bool IsActive { get; set; } = true;
        public RingCanonicalPosition CanonicalPosition { get; set; }
        public float SizeMeters { get; set; } = 0.5f;
    }

    private static ContainmentFieldProfile Prof(double wallTop)
        => ContainmentFieldProfile.FromSnapshot(Snap(wallTop, default), W, RimWallDescription.Current, 40_000.0, 1600.0);

    private static void Walk(BodyContactDisturbanceAdapter adapter, ContainmentFieldProfile p, RingCanonicalPosition anchor, IEnumerable<RingCanonicalPosition> path, ref double now)
    {
        RingLodGeometrySnapshot s = Snap(p.WallTopMeters, anchor);
        foreach (RingCanonicalPosition pos in path) { adapter.Update(now, Dt, s, pos); now += Dt; }
    }

    private static IEnumerable<RingCanonicalPosition> Line(RingCanonicalPosition a, RingCanonicalPosition b, double stepMeters)
    {
        double dx = b.AcrossMeters - a.AcrossMeters, dv = b.VerticalMeters - a.VerticalMeters, da = b.AroundMeters - a.AroundMeters;
        double len = Math.Sqrt(dx * dx + dv * dv + da * da);
        int n = Math.Max(1, (int)Math.Ceiling(len / stepMeters));
        for (int i = 0; i <= n; i++) { double f = (double)i / n; yield return new RingCanonicalPosition(a.AroundMeters + da * f, a.VerticalMeters + dv * f, a.AcrossMeters + dx * f); }
    }

    public int Run()
    {
        try
        {
            ContainmentFieldProfile p = Prof(10_000.0);
            Check(p.IsValid, "profile valid");
            Crossings(p);
            WallTop(p);
            Cooldown(p);
            Ids(p);
            Bodies(p);
            Scaling();
            Weight(p);
            Buses();
            Hum(p);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.ToString());
            Console.Error.WriteLine("CONTAINMENT_BODY_CONTACT=FAIL exception " + e.Message);
            return 1;
        }
        if (_failures.Count != 0) { Console.Error.WriteLine("CONTAINMENT_BODY_CONTACT=FAIL " + string.Join(" | ", _failures.Take(25))); return 1; }
        Console.WriteLine("CONTAINMENT_BODY_CONTACT=PASS");
        return 0;
    }

    private BodyContactDisturbanceAdapter NewAdapter(ContainmentFieldProfile p, IContainmentDisturbanceSink sink) => new(sink, p, Circ);

    // ---- Crossings: the arch (noclip), both directions --------------------------------------------------------------------------------------------

    private void Crossings(ContainmentFieldProfile p)
    {
        double c = p.Centre, top = p.WallTopMeters;
        foreach (double step in new[] { 1.2, 40.0, 150.0 })
        {
            var sink = new ListSink();
            BodyContactDisturbanceAdapter a = NewAdapter(p, sink);
            double now = 0.0;
            var from = new RingCanonicalPosition(2000.0, 39_000.0, c);
            Walk(a, p, from, Line(from, new RingCanonicalPosition(2000.0, 41_000.0, c), step), ref now);
            Check(a.Crossings == 1 && sink.Published.Count == 1, $"arch up step {step}: crossings {a.Crossings} published {sink.Published.Count}");
            if (sink.Published.Count == 1)
            {
                ContainmentDisturbance d = sink.Published[0];
                Check(Math.Abs(d.Position.VerticalMeters - 40_000.0) < 0.05 && Math.Abs(d.Position.AcrossMeters - c) < 0.05, $"arch hit position {d.Position}");
                Check(d.Kind == ContainmentDisturbanceKind.BodyContact && d.DurationSeconds == 1.5f && d.FlashCoupling == 0.0f && d.FlashLevel == null, "arch ripple parameters");
                Check(a.LastCrossing is { ToVacuumSide: true }, "arch up goes to the vacuum side");
            }
            now += 3.0;
            var down = new RingCanonicalPosition(2000.0, 41_000.0, c);
            Walk(a, p, down, Line(down, from, step), ref now);
            Check(a.Crossings == 2 && sink.Published.Count == 2, $"arch down step {step}: crossings {a.Crossings} published {sink.Published.Count}");
            Check(a.LastCrossing is { ToVacuumSide: false }, "arch down goes to the contained side");
        }
        {
            double r0 = p.RootAcross0;
            var sink = new ListSink();
            BodyContactDisturbanceAdapter a = NewAdapter(p, sink);
            double now = 0.0;
            var from = new RingCanonicalPosition(9000.0, top + 800.0, r0 - 100.0);
            Walk(a, p, from, Line(from, new RingCanonicalPosition(9040.0, top + 800.0, r0 + 100.0), 2.0), ref now);
            Check(a.Crossings == 1 && sink.Published.Count == 1, $"skirt in: crossings {a.Crossings}");
            now += 3.0;
            var back = new RingCanonicalPosition(9040.0, top + 800.0, r0 + 100.0);
            Walk(a, p, back, Line(back, from, 2.0), ref now);
            Check(a.Crossings == 2 && sink.Published.Count == 2, $"skirt out: crossings {a.Crossings}");
            double c1 = sink.Published.Count > 0 ? sink.Published[0].Position.AcrossMeters : double.NaN;
            Check(Math.Abs(c1 - r0) < 0.05, $"skirt hit across {c1} vs {r0}");
        }
        {
            double rw = p.RootAcrossW;
            var sink = new ListSink();
            BodyContactDisturbanceAdapter a = NewAdapter(p, sink);
            double now = 0.0;
            var from = new RingCanonicalPosition(9000.0, top + 400.0, rw + 50.0);
            Walk(a, p, from, Line(from, new RingCanonicalPosition(9000.0, top + 400.0, rw - 50.0), 2.0), ref now);
            Check(a.Crossings == 1, $"skirt W crossings {a.Crossings}");
        }
        {
            var sink = new ListSink();
            BodyContactDisturbanceAdapter a = NewAdapter(p, sink);
            RingLodGeometrySnapshot s = Snap(top, new RingCanonicalPosition(2000.0, 30_000.0, c));
            a.Update(0.0, Dt, s, new RingCanonicalPosition(2000.0, 10_000.0, c));
            a.Update(Dt, Dt, s, new RingCanonicalPosition(2000.0, 60_000.0, c));
            Check(a.Crossings == 0, "teleport over the field is not a crossing");
        }
        {
            var sink = new ListSink();
            BodyContactDisturbanceAdapter a = NewAdapter(p, sink);
            double now = 0.0;
            var from = new RingCanonicalPosition(100.0, 20.0, c);
            Walk(a, p, from, Enumerable.Repeat(from, 120), ref now);
            Walk(a, p, from, Line(from, new RingCanonicalPosition(100.0, 20.0, c + 800.0), 0.1), ref now);
            Check(a.Crossings == 0 && sink.Published.Count == 0, "habitat floor: no crossing");
        }
    }

    // ---- Walking the wall top ---------------------------------------------------------------------------------------------------------------------

    private void WallTop(ContainmentFieldProfile p)
    {
        double top = p.WallTopMeters, r0 = p.RootAcross0, rw = p.RootAcrossW, eye = top + 1.7;
        foreach ((double from, double to, string name) in new[] { (r0 - 64.0, r0 + 64.0, "0 in"), (r0 + 64.0, r0 - 64.0, "0 out"), (rw + 64.0, rw - 64.0, "W in"), (rw - 64.0, rw + 64.0, "W out") })
        {
            var sink = new ListSink();
            BodyContactDisturbanceAdapter a = NewAdapter(p, sink);
            double now = 0.0;
            var f = new RingCanonicalPosition(40_000.0, eye, from);
            Walk(a, p, f, Line(f, new RingCanonicalPosition(40_000.0, eye, to), 5.0 * Dt), ref now);
            Check(a.Crossings == 1 && sink.Published.Count == 1, $"wall-top cross {name}: crossings {a.Crossings}");
            if (sink.Published.Count == 1)
            {
                double across = sink.Published[0].Position.AcrossMeters, root = name[0] == '0' ? r0 : rw;
                Check(Math.Abs(across - root) < 0.05 && sink.Published[0].Position.VerticalMeters > top, $"wall-top hit {across} vs root {root}");
            }
        }
        foreach (double offset in new[] { -10.0, -0.3, 0.3, 10.0, 40.0 })
        {
            foreach (double root in new[] { r0, rw })
            {
                var sink = new ListSink();
                BodyContactDisturbanceAdapter a = NewAdapter(p, sink);
                double now = 0.0;
                var f = new RingCanonicalPosition(40_000.0, eye, root + offset);
                Walk(a, p, f, Line(f, new RingCanonicalPosition(40_600.0, eye, root + offset), 5.0 * Dt), ref now);
                Check(a.Crossings == 0 && sink.Published.Count == 0, $"wall-top walk along root {root} offset {offset}: crossings {a.Crossings}");
            }
        }
        {
            var sink = new ListSink();
            BodyContactDisturbanceAdapter a = NewAdapter(p, sink);
            double now = 0.0;
            var f = new RingCanonicalPosition(40_000.0, eye, 10.0);
            Walk(a, p, f, Line(f, new RingCanonicalPosition(40_000.0, eye, r0 + 70.0), 5.0 * Dt), ref now);
            Check(a.Crossings == 0, $"wall top inner half: crossings {a.Crossings}");
        }
    }

    // ---- Cooldown ---------------------------------------------------------------------------------------------------------------------------------

    private void Cooldown(ContainmentFieldProfile p)
    {
        double c = p.Centre;
        var bus = new ContainmentDisturbanceBus(p, Circ) { Observer = new RingCanonicalPosition(2000.0, 39_000.0, c) };
        var adapter = new BodyContactDisturbanceAdapter(bus, p, Circ);
        var heard = new List<ContainmentBodyCrossing>();
        adapter.Crossed += heard.Add;
        RingLodGeometrySnapshot s = Snap(p.WallTopMeters, new RingCanonicalPosition(2000.0, 40_000.0, c));
        double now = 10.0;
        void Step(double v) { bus.Advance(now); adapter.Update(now, Dt, s, new RingCanonicalPosition(2000.0, v, c)); now += Dt; }
        Step(39_990.0);
        Step(40_010.0);
        Check(adapter.Published == 1 && bus.LiveOf(ContainmentDisturbanceKind.BodyContact) == 1, $"first crossing published ({adapter.Published}, live {bus.LiveOf(ContainmentDisturbanceKind.BodyContact)})");
        for (int i = 0; i < 20; i++) Step(40_010.0);
        Step(39_990.0);
        Check(adapter.Crossings == 2 && adapter.Published == 1 && adapter.Suppressed == 1 && heard.Count == 1, $"jitter inside the cooldown: crossings {adapter.Crossings} published {adapter.Published} suppressed {adapter.Suppressed} heard {heard.Count}");
        for (int i = 0; i < 90; i++) Step(39_990.0);
        Step(40_010.0);
        Check(adapter.Crossings == 3 && adapter.Published == 2 && heard.Count == 2, $"after the cooldown: crossings {adapter.Crossings} published {adapter.Published}");
        Check(bus.LiveOf(ContainmentDisturbanceKind.BodyContact) <= 2, "body contact stays within its 2 reserved slots");
        int before = adapter.Published;
        for (int i = 0; i < 180; i++) Step(i % 2 == 0 ? 39_990.0 : 40_010.0);
        Check(adapter.Published - before <= 4, $"hammering published {adapter.Published - before} in 3 s");
        now = 5.0;
        before = adapter.Published;
        bus.Advance(now);
        Step(39_990.0);
        Step(40_010.0);
        Check(adapter.Published > before, "clock back re-arms");
    }

    // ---- SourceIds --------------------------------------------------------------------------------------------------------------------------------

    private void Ids(ContainmentFieldProfile p)
    {
        List<long> Run()
        {
            var sink = new ListSink();
            BodyContactDisturbanceAdapter a = NewAdapter(p, sink);
            double now = 0.0, c = p.Centre;
            var lo = new RingCanonicalPosition(2000.0, 39_000.0, c);
            var hi = new RingCanonicalPosition(2000.0, 41_000.0, c);
            for (int k = 0; k < 4; k++)
            {
                Walk(a, p, lo, Line(k % 2 == 0 ? lo : hi, k % 2 == 0 ? hi : lo, 50.0), ref now);
                now += 2.0;
            }
            return sink.Published.Select(d => d.SourceId).ToList();
        }
        List<long> r1 = Run(), r2 = Run();
        Check(r1.Count == 4 && r1.SequenceEqual(r2), $"deterministic ids ({r1.Count} vs {r2.Count})");
        Check(r1.Distinct().Count() == r1.Count, "ids distinct per crossing");
        Check(BodyContactDisturbanceAdapter.SourceIdOf(1, 1) != BodyContactDisturbanceAdapter.SourceIdOf(2, 1) && BodyContactDisturbanceAdapter.SourceIdOf(1, 1) != BodyContactDisturbanceAdapter.SourceIdOf(1, 2), "ids differ per body and per count");
        Check(r1.Count > 0 && r1[0] == BodyContactDisturbanceAdapter.SourceIdOf(BodyContactDisturbanceAdapter.PlayerBodyId, 1), "player id = SourceIdOf(player, count)");
    }

    // ---- Registered bodies ------------------------------------------------------------------------------------------------------------------------

    private void Bodies(ContainmentFieldProfile p)
    {
        double c = p.Centre;
        var sink = new ListSink();
        BodyContactDisturbanceAdapter a = NewAdapter(p, sink);
        var body = new Body { CanonicalPosition = new RingCanonicalPosition(3000.0, 38_000.0, c) };
        a.Register(body); a.Register(body);
        Check(a.RegisteredBodies == 1, "register is idempotent");
        RingLodGeometrySnapshot s = Snap(p.WallTopMeters, new RingCanonicalPosition(3000.0, 40_000.0, c));
        var player = new RingCanonicalPosition(100.0, 20.0, c);
        double now = 0.0;
        for (int i = 0; i < 60; i++)
        {
            body.CanonicalPosition = new RingCanonicalPosition(3000.0, 38_000.0 + 50.0 * i, c);
            a.Update(now, Dt, s, player); now += Dt;
        }
        Check(a.Crossings == 1 && sink.Published.Count == 1, $"body crossing {a.Crossings}");
        if (sink.Published.Count == 1)
        {
            ContainmentDisturbance d = sink.Published[0];
            Check(d.RadiusMeters >= 2.0f && d.RadiusMeters <= 30.0f && d.SourceId == BodyContactDisturbanceAdapter.SourceIdOf(7, 1), $"body ripple radius {d.RadiusMeters} id");
            Check(a.LastCrossing is { IsPlayer: false, SpeedMetersPerSecond: > 2000.0f }, "body speed from the segment");
        }
        body.IsActive = false;
        a.Update(now, Dt, s, player);
        body.IsActive = true; a.Unregister(body);
        Check(a.RegisteredBodies == 0, "unregistered");
    }

    private void Scaling()
    {
        float walk = BodyContactDisturbanceAdapter.RadiusFor(5.0f, 1.8f), noclip = BodyContactDisturbanceAdapter.RadiusFor(72.0f, 1.8f);
        float fast = BodyContactDisturbanceAdapter.RadiusFor(1200.0f, 1.8f), tiny = BodyContactDisturbanceAdapter.RadiusFor(0.0f, 1.0f), huge = BodyContactDisturbanceAdapter.RadiusFor(1e9f, 100.0f);
        Check(tiny == 2.0f && huge == 30.0f && walk > tiny && noclip > walk && fast > noclip && fast <= 30.0f, $"radius scaling {tiny} {walk} {noclip} {fast} {huge}");
        Check(BodyContactDisturbanceAdapter.RadiusFor(10.0f, 4.0f) > BodyContactDisturbanceAdapter.RadiusFor(10.0f, 1.0f), "radius grows with size");
        Check(BodyContactDisturbanceAdapter.EnergyFor(0.0f, 1.0f) is > 0.0f and < 1.0f && BodyContactDisturbanceAdapter.EnergyFor(1e9f, 100.0f) == 1.0f, "energy range");
    }

    // ---- WO10: the vacuum side as a function of signed distance -----------------------------------------------------------------------------------

    private void Weight(ContainmentFieldProfile p)
    {
        double c = p.Centre;
        float prev = -1.0f, maxStep = 0.0f;
        for (double sd = -10.0; sd <= 10.0; sd += 0.05)
        {
            float w = RingContainmentFieldAudio.WeightOfSignedDistance(sd);
            Check(w >= prev - 1e-6f, $"weight monotonic at {sd}");
            if (prev >= 0.0f) maxStep = Math.Max(maxStep, w - prev);
            prev = w;
        }
        Check(maxStep < 0.04f, $"weight ramp smooth (max step per 5 cm {maxStep})");
        Check(RingContainmentFieldAudio.WeightOfSignedDistance(-3.0) == 0.0f && RingContainmentFieldAudio.WeightOfSignedDistance(3.0) == 1.0f
            && Math.Abs(RingContainmentFieldAudio.WeightOfSignedDistance(0.0) - 0.5f) < 1e-6f, "ramp ends and midpoint");
        Check(Math.Abs(RingContainmentFieldAudio.CutoffFor(0.0f) - RingContainmentFieldAudio.OpenCutoffHz) < 1e-3f && Math.Abs(RingContainmentFieldAudio.CutoffFor(1.0f) - RingContainmentFieldAudio.VacuumCutoffHz) < 1e-3f
            && RingContainmentFieldAudio.CutoffFor(0.5f) < RingContainmentFieldAudio.CutoffFor(0.25f), "cutoff curve");
        Check(RingContainmentFieldAudio.GainDbFor(0.0f) == 0.0f && RingContainmentFieldAudio.GainDbFor(1.0f) == RingContainmentFieldAudio.VacuumGainDb, "gain curve");

        Check(RingContainmentFieldAudio.VacuumWeight(p, new(0.0, 20.0, c)) == 0.0f, "floor: contained");
        Check(RingContainmentFieldAudio.VacuumWeight(p, new(0.0, 39_990.0, c)) == 0.0f && RingContainmentFieldAudio.VacuumWeight(p, new(0.0, 40_010.0, c)) == 1.0f, "apex +-10 m");
        Check(Math.Abs(RingContainmentFieldAudio.VacuumWeight(p, new(0.0, 40_000.0, c)) - 0.5f) < 1e-3f, "apex 0.5");
        float last = -1.0f; bool mono = true;
        for (double v = 39_995.0; v <= 40_005.0; v += 0.1) { float w = RingContainmentFieldAudio.VacuumWeight(p, new(0.0, v, c)); mono &= w >= last - 1e-6f; last = w; }
        Check(mono, "weight along a climb is monotonic");
        double top = p.WallTopMeters, r0 = p.RootAcross0;
        Check(RingContainmentFieldAudio.VacuumWeight(p, new(0.0, top + 1.7, r0 + 20.0)) == 0.0f, "wall top inboard of the root: contained");
        Check(RingContainmentFieldAudio.VacuumWeight(p, new(0.0, top + 1.7, r0 - 20.0)) == 1.0f, "wall top outboard of the root: vacuum side");
        Check(RingContainmentFieldAudio.VacuumWeight(p, new(0.0, top + 1.7, r0 - 1.0)) is > 0.5f and < 1.0f, "wall top 1 m outboard: partway");
        Check(RingContainmentFieldAudio.VacuumWeight(p, new(0.0, top - 30.0, r0 - 40.0)) == 0.0f, "inside the outer wall body: not vacuum");
    }

    // ---- Field hum hook ---------------------------------------------------------------------------------------------------------------------------

    private void Hum(ContainmentFieldProfile p)
    {
        // No shipped asset: the slot is empty, so the hum is silently off.
        Check(RingContainmentFieldAudio.LoadHum() == null, "no field_hum_loop file: no hum stream");
        // The crossfade at the specified distances: directional beyond 300 m, stereo within 20 m, equal-power throughout, silent beyond 3 km.
        (float, float) Gains(double d) => RingContainmentFieldAudio.HumGains(d);
        (float pos0, float st0) = Gains(0.0);
        Check(Math.Abs(pos0) < 1e-6f && Math.Abs(st0 - 1.0f) < 1e-6f, $"0 m: stereo only ({pos0}, {st0})");
        (float pos20, float st20) = Gains(20.0);
        Check(pos20 < 1e-3f && st20 > 0.99f, $"20 m: stereo ({pos20}, {st20})");
        (float pos300, float st300) = Gains(300.0);
        Check(st300 < 1e-6f && pos300 > 0.5f && pos300 < 1.0f, $"300 m: directional only ({pos300}, {st300})");
        (float pos150, float st150) = Gains(150.0);
        Check(pos150 > 0.1f && st150 > 0.1f, $"150 m: both voices ({pos150}, {st150})");
        (float pos1000, float st1000) = Gains(1000.0);
        Check(st1000 == 0.0f && pos1000 > 0.0f && pos1000 < pos300, $"1000 m: directional, quieter ({pos1000}, {st1000})");
        (float pos4000, float st4000) = Gains(4000.0);
        Check(pos4000 == 0.0f && st4000 == 0.0f, "4000 m: silent");
        float lastLoud = -1.0f, lastBlend = -1.0f;
        for (double d = 3000.0; d >= 0.0; d -= 1.0)
        {
            (float a, float b) = Gains(d);
            float loud = RingContainmentFieldAudio.HumLoudness(d);
            Check(Math.Abs(a * a + b * b - loud * loud) < 1e-5f, $"equal power at {d} m");
            Check(loud >= lastLoud - 1e-6f && RingContainmentFieldAudio.HumBlend(d) >= lastBlend - 1e-6f, $"loudness and blend rise as the distance shrinks at {d} m");
            lastLoud = loud; lastBlend = RingContainmentFieldAudio.HumBlend(d);
        }
        Check(RingContainmentFieldAudio.HumLoudness(3000.0) == 0.0f && RingContainmentFieldAudio.HumLoudness(0.0) == 1.0f, "loudness ends");

        // A stand-in stream (never shipped) on the voices: the volumes follow the gains and the menu volume; nothing plays beyond the range.
        var audio = new RingContainmentFieldAudio();
        audio._Ready();
        var wav = new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = 44100, Data = new byte[4410 * 2] };
        audio.SetHumStream(wav);
        Check(audio.HumStream != null, "stand-in stream injected");
        float saved = RingContainmentFieldAudio.HumVolumeDb;
        RingContainmentFieldAudio.HumVolumeDb = 0.0f;
        audio.ApplyHumGains(150.0);
        float expectPos = RingContainmentFieldAudio.HumBaseDb + Mathf.LinearToDb(pos150), expectSt = RingContainmentFieldAudio.HumBaseDb + Mathf.LinearToDb(st150);
        Check(Math.Abs(audio.HumPositionalDb - expectPos) < 1e-3f && Math.Abs(audio.HumStereoDb - expectSt) < 1e-3f, $"voice volumes {audio.HumPositionalDb} {audio.HumStereoDb} vs {expectPos} {expectSt}");
        RingContainmentFieldAudio.HumVolumeDb = -6.0f;
        audio.ApplyHumGains(150.0);
        Check(Math.Abs(audio.HumPositionalDb - (expectPos - 6.0f)) < 1e-3f, "menu volume adds in dB");
        RingContainmentFieldAudio.HumVolumeDb = saved;
        audio.ApplyHumGains(4000.0);
        Check(audio.HumPositionalDb == RingContainmentFieldAudio.SilentDb && audio.HumStereoDb == RingContainmentFieldAudio.SilentDb, "beyond range both voices silent");
        // From the floor the field is more than 3 km away: UpdateHum plays nothing.
        RingLodGeometrySnapshot snap = Snap(p.WallTopMeters, new RingCanonicalPosition(0.0, 20.0, p.Centre));
        audio.UpdateHum(p, snap, new RingCanonicalPosition(0.0, 20.0, p.Centre), Dt);
        Check(!audio.HumPlaying && audio.HumPositionalGain == 0.0f && audio.HumStereoGain == 0.0f && audio.HumDistanceMeters > 3000.0, $"floor: no voice playing (distance {audio.HumDistanceMeters})");
        audio.SetHumStream(null);
        audio.UpdateHum(p, snap, new RingCanonicalPosition(0.0, 39_000.0, p.Centre), Dt);
        Check(!audio.HumPlaying, "no stream: silent even in range");
        // The hum bus gets the vacuum low-pass like the weather buses.
        Check(Array.IndexOf(RingContainmentFieldAudio.VacuumBuses, RingContainmentFieldAudio.HumBus) >= 0, "hum bus is in the vacuum list");
        int bus = AudioServer.GetBusIndex(RingContainmentFieldAudio.HumBus);
        Check(bus >= 0, "ContainmentField bus exists");
        audio.ApplyWeight(1.0f, 0.0);
        Check(audio.IsEngagedOn(RingContainmentFieldAudio.HumBus), "vacuum effect engaged on the hum bus");
        audio.ReleaseBuses();
        audio.Free();
        if (bus >= 0) AudioServer.RemoveBus(AudioServer.GetBusIndex(RingContainmentFieldAudio.HumBus));
    }

    private void Buses()
    {
        string name = "Thunder";
        int bus = AudioServer.GetBusIndex(name);
        bool added = false;
        if (bus < 0) { AudioServer.AddBus(); bus = AudioServer.BusCount - 1; AudioServer.SetBusName(bus, name); added = true; }   // headless script runs may not load the layout
        int count = AudioServer.GetBusEffectCount(bus);
        var audio = new RingContainmentFieldAudio();
        audio.ApplyWeight(0.0f, 1.0 / 60.0);
        Check(AudioServer.GetBusEffectCount(bus) == count && !audio.IsEngagedOn(name), "idle: nothing installed");
        audio.ApplyWeight(1.0f, 1.0 / 60.0);
        Check(audio.Weight > 0.0f && audio.Weight < 0.5f, $"one frame ramps, not jumps ({audio.Weight})");
        for (int i = 0; i < 120; i++) audio.ApplyWeight(1.0f, 1.0 / 60.0);
        Check(AudioServer.GetBusEffectCount(bus) == count + 2 && audio.IsEngagedOn(name), "engaged: filter and amplifier added");
        Check(Math.Abs(audio.CutoffHz - RingContainmentFieldAudio.VacuumCutoffHz) < 5.0f && Math.Abs(audio.GainDb - RingContainmentFieldAudio.VacuumGainDb) < 0.1f, $"vacuum values {audio.CutoffHz} Hz {audio.GainDb} dB");
        bool sawFilter = false, sawAmp = false;
        for (int i = 0; i < AudioServer.GetBusEffectCount(bus); i++)
        {
            AudioEffect e = AudioServer.GetBusEffect(bus, i);
            if (e is AudioEffectLowPassFilter f && Math.Abs(f.CutoffHz - RingContainmentFieldAudio.VacuumCutoffHz) < 5.0f) sawFilter = true;
            if (e is AudioEffectAmplify amp && Math.Abs(amp.VolumeDb - RingContainmentFieldAudio.VacuumGainDb) < 0.1f) sawAmp = true;
        }
        Check(sawFilter && sawAmp, "bus effect values set");
        for (int i = 0; i < 240; i++) audio.ApplyWeight(0.0f, 1.0 / 60.0);
        Check(audio.Weight == 0.0f && !audio.IsEngagedOn(name) && audio.GainDb == 0.0f, "back inside: bypassed");
        audio.ReleaseBuses();
        Check(AudioServer.GetBusEffectCount(bus) == count, "released: effect count restored");
        audio.Free();
        if (added) AudioServer.RemoveBus(AudioServer.GetBusIndex(name));
    }
}
