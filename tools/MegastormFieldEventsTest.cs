using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Megastorm WO8 (docs/megastorm-design.md 6.3 and 8): the field publishing seam, against a stub sink and a stub provider (the containment field is
/// not built yet). Shared with the field's coupling test through tools/fixtures/containment_megastorm_fixture.json: a triggered megastorm at a fixed
/// weather time with the wall at a rim on a Certified (wall top 10 km) and a runtime-surface (10.4 km) profile, plus the expected contact SourceIds,
/// burst pulse ids and sprite ids. Checks: deterministic SourceIds, contact positions within the band (6.2 / 5.8 km) and on the wall (xi 0-25 km),
/// burst pulses advancing by v x 1.5 s, at most 4 ripples per second, frame-rate independence, nothing without a provider or beyond 400 km, and one id
/// per sprite discharge, identical to what the natural sprite layer's TryDerive makes.
/// Run: tools/megastorm_field_events_test.gd (headless). Prints MEGASTORM_FIELD_EVENTS=PASS. Set MEGASTORM_FIELD_FIXTURE_WRITE=1 to rewrite the fixture.
/// </summary>
public partial class MegastormFieldEventsTest : RefCounted
{
    const string FixturePath = "res://tools/fixtures/containment_megastorm_fixture.json";
    const double DefaultApexMeters = 40000.0, Speed = 350.0;

    // ---- fixture schema (camelCase JSON; the field's test reads the same file) ----
    sealed class FixtureModel
    {
        public string TriggerId { get; set; } = "containment_megastorm_fixture";
        public double LeadingAroundMeters { get; set; } = 5_000_000.0;
        public int Direction { get; set; } = 1;
        public double TriggerTimeSeconds { get; set; }
        public float SpeedMetersPerSecond { get; set; } = 350.0f;
        public float FormationSeconds { get; set; } = 1800.0f;
        public int Seed { get; set; } = 7;
    }

    sealed class FixtureExpected
    {
        public double BandEdgeMeters { get; set; }
        public int ContactCount { get; set; }
        public List<string> ContactSourceIds { get; set; } = new();
    }

    sealed class FixtureProfile
    {
        public string Name { get; set; } = "";
        public double WallTopMeters { get; set; }
        public double ApexMeters { get; set; } = DefaultApexMeters;
        public double MaxContactRimMeters { get; set; }
        public FixtureExpected Expected { get; set; } = new();
    }

    sealed class FixtureBurst
    {
        public string Id { get; set; } = "";
        public double StartSeconds { get; set; }
        public double AcrossMeters { get; set; }
        public List<string> PulseSourceIds { get; set; } = new();
    }

    sealed class Fixture
    {
        public int Version { get; set; } = 1;
        public string Note { get; set; } = "Shared by MegastormFieldEventsTest (megastorm WO8) and the containment field coupling test. SourceIds are decimal strings.";
        public FixtureModel Model { get; set; } = new();
        public double CircumferenceMeters { get; set; } = 31415936.0;
        public double WidthMeters { get; set; } = 318000.0;
        public double WeatherTimeSeconds { get; set; } = 6000.0;
        public double WindowSeconds { get; set; } = 12.0;
        /// <summary>The camera stands this far behind the reference line at WeatherTimeSeconds.</summary>
        public double CameraXiMeters { get; set; } = 12000.0;
        public double CameraAcrossMeters { get; set; } = 159000.0;
        public List<FixtureProfile> Profiles { get; set; } = new()
        {
            new FixtureProfile { Name = "certified", WallTopMeters = 10000.0, MaxContactRimMeters = 6200.0 },
            new FixtureProfile { Name = "runtime_surface", WallTopMeters = 10400.0, MaxContactRimMeters = 5800.0 },
        };
        public List<FixtureBurst> Bursts { get; set; } = new();
        public List<string> SpriteEventIds { get; set; } = new();
    }

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    sealed class StubSink : IContainmentDisturbanceSink
    {
        public readonly List<ContainmentDisturbance> Items = new();
        readonly HashSet<long> _ids = new();
        public bool Publish(in ContainmentDisturbance d)
        {
            if (!_ids.Add(d.SourceId)) return false;
            Items.Add(d);
            return true;
        }
    }

    public int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        Func<double, double>? savedProvider = MegastormFieldCoupling.FieldUndersideProvider;
        Func<double, double> savedCeiling = MegastormFieldCoupling.CeilingFromUnderside;
        try
        {
            string path = ProjectSettings.GlobalizePath(FixturePath);
            bool write = System.Environment.GetEnvironmentVariable("MEGASTORM_FIELD_FIXTURE_WRITE") == "1";
            Fixture fixture;
            if (write) fixture = BuildFixture();
            else if (!File.Exists(path)) { Console.Error.WriteLine("MEGASTORM_FIELD_EVENTS=FAIL fixture missing: " + path); return 1; }
            else fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path), Json) ?? throw new JsonException("empty fixture");
            RunChecks(fixture, Check, write);
            if (write)
            {
                File.WriteAllText(path, JsonSerializer.Serialize(fixture, Json) + "\n");
                Console.WriteLine("MEGASTORM_FIELD_EVENTS_INFO wrote " + path);
            }
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); Console.Error.WriteLine("MEGASTORM_FIELD_EVENTS=FAIL exception " + e.Message); return 1; }
        finally
        {
            MegastormFieldCoupling.FieldUndersideProvider = savedProvider;
            MegastormFieldCoupling.CeilingFromUnderside = savedCeiling;
        }
        if (failures.Count != 0) { Console.Error.WriteLine("MEGASTORM_FIELD_EVENTS=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("MEGASTORM_FIELD_EVENTS=PASS");
        return 0;
    }

    static Fixture BuildFixture() => new();

    static MegastormModel MakeModel(Fixture f)
    {
        var model = new MegastormModel(new MegastormSettings { CircumferenceMeters = f.CircumferenceMeters, WidthMeters = f.WidthMeters });
        FixtureModel m = f.Model;
        model.Trigger(new MegastormTrigger(m.TriggerId, m.LeadingAroundMeters, m.Direction, m.TriggerTimeSeconds, m.SpeedMetersPerSecond, m.FormationSeconds, m.Seed));
        return model;
    }

    /// <summary>The stub field underside: a semi-ellipse from the wall top to the apex (containment-field-design 1.2).</summary>
    static Func<double, double> Provider(double wallTop, double apex, double width)
    {
        double a = 0.5 * width, b = apex - wallTop;
        return across =>
        {
            if (across < 0.0 || across > width) return double.NaN;
            double x = (across - a) / a;
            return wallTop + b * Math.Sqrt(Math.Max(0.0, 1.0 - x * x));
        };
    }

    static RingCanonicalPosition Camera(MegastormModel model, Fixture f, double t)
        => new(MegastormModel.WrapPositive(model.AroundAt(t) - model.State.Direction * f.CameraXiMeters, f.CircumferenceMeters), 0.0, f.CameraAcrossMeters);

    /// <summary>Runs a publisher from t0 for <paramref name="seconds"/> in steps of dt, the camera fixed (or following the wall).</summary>
    static StubSink RunWindow(MegastormModel model, Fixture f, double t0, double seconds, double dt, bool follow, RingCanonicalPosition? fixedCamera = null)
    {
        var sink = new StubSink();
        var publisher = new MegastormFieldPublisher(model, sink);
        RingCanonicalPosition camera = fixedCamera ?? Camera(model, f, t0);
        int steps = (int)Math.Round(seconds / dt);
        for (int i = 0; i <= steps; i++)
        {
            double t = t0 + i * dt;
            if (follow) camera = Camera(model, f, t);
            publisher.Update(t, camera);
        }
        return sink;
    }

    static string Id(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    static double RimDistance(double across, double width) => Math.Min(across, width - across);

    void RunChecks(Fixture f, Action<bool, string> check, bool write)
    {
        MegastormModel model = MakeModel(f);
        double t0 = f.WeatherTimeSeconds, c = f.CircumferenceMeters, w = f.WidthMeters;
        void Info(string m) => Console.WriteLine("MEGASTORM_FIELD_EVENTS_INFO " + m);

        // ---- 1. per profile: fixture ids, determinism, band, wall, cap ----
        foreach (FixtureProfile profile in f.Profiles)
        {
            MegastormFieldCoupling.FieldUndersideProvider = Provider(profile.WallTopMeters, profile.ApexMeters, w);
            string tag = profile.Name;
            double edge = MegastormFieldCoupling.BandEdgeMeters(w);
            check(edge > 0.0 && edge <= profile.MaxContactRimMeters + 15.0, $"{tag}: band edge {edge:0.0} m is not within {profile.MaxContactRimMeters} m");
            check(Math.Abs(edge - profile.MaxContactRimMeters) < 600.0, $"{tag}: band edge {edge:0.0} m is far from the doc width {profile.MaxContactRimMeters} m");

            StubSink a = RunWindow(model, f, t0, f.WindowSeconds, 0.25, false);
            StubSink b = RunWindow(model, f, t0, f.WindowSeconds, 0.25, false);
            var contactA = a.Items.Where(d => d.Kind == ContainmentDisturbanceKind.MegastormContact).ToList();
            check(contactA.Count > 0, $"{tag}: no contact ripples in the fixture window");
            check(a.Items.Select(d => d.SourceId).SequenceEqual(b.Items.Select(d => d.SourceId)), $"{tag}: SourceIds differ between two runs");
            check(a.Items.Select(d => d.SourceId).Distinct().Count() == a.Items.Count, $"{tag}: duplicate SourceIds in a window");
            if (write)
            {
                profile.Expected.BandEdgeMeters = Math.Round(edge, 1);
                profile.Expected.ContactCount = contactA.Count;
                profile.Expected.ContactSourceIds = contactA.Select(d => Id(d.SourceId)).ToList();
            }
            else
            {
                check(Math.Abs(edge - profile.Expected.BandEdgeMeters) <= 1.0, $"{tag}: band edge {edge:0.0} vs fixture {profile.Expected.BandEdgeMeters:0.0}");
                check(contactA.Count == profile.Expected.ContactCount, $"{tag}: contact count {contactA.Count} vs fixture {profile.Expected.ContactCount}");
                check(contactA.Select(d => Id(d.SourceId)).SequenceEqual(profile.Expected.ContactSourceIds), $"{tag}: contact SourceIds differ from the fixture");
            }
            Info($"{tag}: band {edge:0.0} m, {contactA.Count} contact ripples in {f.WindowSeconds:0} s");

            // Positions: within the band, on the wall, under the ceiling, payload ranges.
            foreach (ContainmentDisturbance d in contactA)
            {
                double rim = RimDistance(d.Position.AcrossMeters, w);
                double xi = model.Xi(d.Position.AroundMeters, d.Position.AcrossMeters, d.StartTimeSeconds);
                check(rim <= profile.MaxContactRimMeters, $"{tag}: contact {rim:0.0} m from a wall (limit {profile.MaxContactRimMeters})");
                check(xi >= -1.0 && xi <= MegastormModel.WallEndXi + 1.0, $"{tag}: contact xi {xi:0.0} m is off the wall (0..25 km)");
                check(d.Energy > 0.0f && d.Energy <= 1.0f && d.DurationSeconds == 5.0f && d.RadiusMeters >= 10000.0f && d.RadiusMeters <= 30000.0f
                    && d.FlashCoupling == 0.6f && d.OccluderRadiusMeters == 12000.0f && d.OccluderCenter == d.Position, $"{tag}: contact payload out of range");
                check(Math.Abs(d.OccluderTopMeters - MegastormFieldCoupling.CeilingAt(d.Position.AcrossMeters)) < 1e-3, $"{tag}: occluder top is not the local ceiling");
                float flash = d.FlashLevel!(d.StartTimeSeconds);
                check(flash >= 0.0f && flash <= 1.0f, $"{tag}: flash level {flash} outside 0..1");
                if (!contactA.Any(x => x.Position.AcrossMeters != d.Position.AcrossMeters)) check(false, $"{tag}: all contacts share one across");
            }
            check(contactA.Any(d => d.Position.AcrossMeters < 0.5 * w) && contactA.Any(d => d.Position.AcrossMeters > 0.5 * w), $"{tag}: contact must appear on both walls");
            // Frame-rate independence: 60 fps and 4 fps produce the same ids.
            StubSink fast = RunWindow(model, f, t0, f.WindowSeconds, 1.0 / 60.0, false);
            StubSink slow = RunWindow(model, f, t0, f.WindowSeconds, 0.25, false);
            check(fast.Items.Select(d => d.SourceId).OrderBy(x => x).SequenceEqual(slow.Items.Select(d => d.SourceId).OrderBy(x => x)) || Math.Abs(fast.Items.Count - slow.Items.Count) <= 1,
                $"{tag}: 60 fps and 4 fps publish different ids");

            // Cap over a long sweep with the camera on the wall: at most 4 per second, contact and bursts combined; burst pulses drift with the storm.
            StubSink sweep = RunWindow(model, f, t0, 7200.0, 0.25, true);
            var perSecond = new Dictionary<long, int>();
            foreach (ContainmentDisturbance d in sweep.Items) { long s = (long)Math.Floor(d.StartTimeSeconds); perSecond[s] = perSecond.GetValueOrDefault(s) + 1; }
            int maxPerSecond = perSecond.Values.DefaultIfEmpty(0).Max();
            int bursts = sweep.Items.Count(d => d.Kind == ContainmentDisturbanceKind.UpdraftBurst);
            Info($"{tag}: sweep 7200 s, {sweep.Items.Count} ripples ({bursts} burst pulses), max {maxPerSecond}/s");
            check(maxPerSecond <= 4, $"{tag}: {maxPerSecond} ripples in one second (cap 4)");
            check(bursts > 0, $"{tag}: no updraft pulse was published in a 2 h sweep");
            var lastPulse = new Dictionary<double, ContainmentDisturbance>();
            int pairs = 0;
            foreach (ContainmentDisturbance d in sweep.Items.Where(d => d.Kind == ContainmentDisturbanceKind.UpdraftBurst))
            {
                check(d.Energy >= MegastormFieldPublisher.MinBurstEnergy && d.DurationSeconds == 5.0f && d.RadiusMeters >= 10000.0f && d.RadiusMeters <= 20000.0f,
                    $"{tag}: burst payload out of range");
                if (lastPulse.TryGetValue(d.Position.AcrossMeters, out ContainmentDisturbance prev) && Math.Abs(d.StartTimeSeconds - prev.StartTimeSeconds - 1.5) < 1e-9)
                {
                    double advance = MegastormModel.WrapSigned(d.Position.AroundMeters - prev.Position.AroundMeters, c) * model.State.Direction;
                    check(Math.Abs(advance - Speed * 1.5) < 1e-3, $"{tag}: burst pulse advanced {advance:0.000} m, expected {Speed * 1.5}");
                    pairs++;
                }
                lastPulse[d.Position.AcrossMeters] = d;
            }
            check(pairs >= 5, $"{tag}: only {pairs} consecutive burst pulse pairs checked");
            Info($"{tag}: {pairs} consecutive pulse pairs advanced v x 1.5 s");
        }

        // ---- 2. burst pulse model: positions advance by v x 1.5 s per pulse, ids per (burst, pulse), fixture ids ----
        {
            var live = new List<MegastormFieldCoupling.UpdraftBurst>();
            MegastormFieldCoupling.BurstsAt(model, t0, live);
            check(live.Count > 0, "no live burst at the fixture time");
            foreach (var burst in live)
            {
                for (int k = 0; k < 4; k++)
                {
                    double advance = MegastormModel.WrapSigned(MegastormFieldCoupling.PulseAround(model, burst, k + 1) - MegastormFieldCoupling.PulseAround(model, burst, k), c) * model.State.Direction;
                    check(Math.Abs(advance - Speed * 1.5) < 1e-3, $"burst {burst.Id} pulse {k}: advance {advance:0.000}");
                }
                double xi = burst.XMeters - model.LeadOffset(burst.AcrossMeters, burst.StartSeconds);
                check(xi >= 3000.0 - 1e-6 && xi <= 18000.0 + 1e-6, $"burst {burst.Id}: spawn xi {xi:0.0} outside 3..18 km");
                check(burst.LifeSeconds >= 120.0 && burst.LifeSeconds <= 600.0, $"burst {burst.Id}: life {burst.LifeSeconds:0.0}");
            }
            if (write)
                f.Bursts = live.Take(4).Select(bu => new FixtureBurst { Id = Id(bu.Id), StartSeconds = bu.StartSeconds, AcrossMeters = bu.AcrossMeters,
                    PulseSourceIds = Enumerable.Range(0, 3).Select(k => Id(MegastormFieldCoupling.Mix64(bu.Id, k, 0, 7))).ToList() }).ToList();
            else
            {
                check(f.Bursts.Count > 0, "fixture lists no bursts");
                foreach (FixtureBurst fb in f.Bursts)
                {
                    var match = live.FirstOrDefault(bu => Id(bu.Id) == fb.Id);
                    check(match.Id != 0 || fb.Id == "0", $"fixture burst {fb.Id} is not alive");
                    check(Math.Abs(match.StartSeconds - fb.StartSeconds) < 1e-6 && Math.Abs(match.AcrossMeters - fb.AcrossMeters) < 1e-3, $"fixture burst {fb.Id}: start or across differ");
                    check(Enumerable.Range(0, 3).Select(k => Id(MegastormFieldCoupling.Mix64(match.Id, k, 0, 7))).SequenceEqual(fb.PulseSourceIds), $"fixture burst {fb.Id}: pulse ids differ");
                }
            }
            Info($"{live.Count} bursts alive at t={t0:0}");
        }

        // ---- 3. nothing without a provider, nothing with the wall beyond 400 km, nothing before the trigger ----
        {
            MegastormFieldCoupling.FieldUndersideProvider = null;
            check(RunWindow(model, f, t0, 30.0, 0.25, true).Items.Count == 0, "published with a null provider");
            check(MegastormFieldCoupling.CeilingAt(5000.0) == RingCloudLayers.CeilingMeters, "null provider ceiling must be the 18 km cap");
            FixtureProfile p0 = f.Profiles[0];
            MegastormFieldCoupling.FieldUndersideProvider = Provider(p0.WallTopMeters, p0.ApexMeters, w);
            var far = new RingCanonicalPosition(MegastormModel.WrapPositive(model.AroundAt(t0) + 450_000.0, c), 0.0, 0.5 * w);
            check(RunWindow(model, f, t0, 30.0, 0.25, false, far).Items.Count == 0, "published with the wall beyond 400 km");
            var near = new RingCanonicalPosition(MegastormModel.WrapPositive(model.AroundAt(t0) - 12000.0, c), 0.0, 0.5 * w);
            check(RunWindow(model, f, t0, 30.0, 0.25, false, near).Items.Count > 0, "published nothing with the wall in range");
            var untriggered = new MegastormModel(new MegastormSettings { CircumferenceMeters = c, WidthMeters = w });
            var sink = new StubSink();
            var pub = new MegastormFieldPublisher(untriggered, sink);
            for (double t = t0; t < t0 + 30.0; t += 0.25) pub.Update(t, near);
            check(sink.Items.Count == 0, "published for an untriggered storm");
            // A paused clock publishes each slot once; a backwards jump publishes nothing.
            var paused = new StubSink();
            var pp = new MegastormFieldPublisher(model, paused);
            pp.Update(t0, near);
            int afterFirst = paused.Items.Count;
            for (int i = 0; i < 20; i++) pp.Update(t0, near);
            check(paused.Items.Count == afterFirst, "a paused clock published again");
            check(pp.Update(t0 - 100.0, near) == 0, "a backwards jump published");
        }

        // ---- 4. sprites: one id per discharge, shared with the natural layer ----
        {
            var sprites = new List<UpperAtmosphereSprite>();
            var flashes = new List<MegastormLightning.MegastormFlash>();
            double spriteEnd = t0 + 600.0;
            MegastormLightning.FlashesInWindow(model, t0, spriteEnd, flashes);
            MegastormFieldCoupling.SpritesInWindow(model, t0, spriteEnd, sprites, 1.0f);
            check(flashes.Count > 200, $"only {flashes.Count} flashes in 600 s");
            check(sprites.Count > 0 && sprites.Count < flashes.Count, $"{sprites.Count} sprites of {flashes.Count} flashes");
            check(sprites.Select(s => s.EventId).Distinct().Count() == sprites.Count, "two sprites share an event id");
            var flashIds = new HashSet<long>(flashes.Select(x => x.Id));
            check(sprites.All(s => flashIds.Contains(s.EventId)), "a sprite id is not a flash id");
            int mismatches = 0;
            foreach (var flash in flashes)
            {
                RingLightningEvent? e = MegastormFieldCoupling.FlashEvent(model, flash);
                bool natural = e is RingLightningEvent ev && UpperAtmosphereLightning.TryDerive(ev, model.Evaluate(flash.StartTimeSeconds).TopMeters, out UpperAtmosphereSprite ns, 1.0f)
                    && ns.EventId == flash.Id;
                bool ours = MegastormFieldCoupling.TryDeriveSprite(model, flash, out UpperAtmosphereSprite os, 1.0f);
                if (natural != ours) mismatches++;
                else if (ours && e is RingLightningEvent ev2)
                {
                    UpperAtmosphereLightning.TryDerive(ev2, model.Evaluate(flash.StartTimeSeconds).TopMeters, out UpperAtmosphereSprite ns2, 1.0f);
                    if (ns2 != os || os.EventId != flash.Id) mismatches++;
                }
            }
            check(mismatches == 0, $"{mismatches} sprites differ from the natural layer's TryDerive");
            double share = sprites.Count / (double)flashes.Count;
            check(Math.Abs(share - UpperAtmosphereLightning.ChanceSuperstorm) < 0.1, $"sprite share {share:0.000} is not near the superstorm chance");
            var eventIds = new HashSet<long>(flashes.Select(x => x.Id));
            check(eventIds.Count == flashes.Count, "flash ids are not unique");
            List<string> ids = sprites.Where(s => s.StartTimeSeconds < t0 + 60.0).Take(8).Select(s => Id(s.EventId)).ToList();
            if (write) f.SpriteEventIds = ids;
            else check(ids.SequenceEqual(f.SpriteEventIds), "sprite event ids differ from the fixture");
            Info($"{sprites.Count} sprites of {flashes.Count} flashes (share {share:0.000})");
        }
    }
}
