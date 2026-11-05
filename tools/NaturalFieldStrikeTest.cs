using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Containment field WO14 (docs/containment-field-design.md 3.10.6): natural near-wall field strikes. Pure checks on <see cref="NaturalFieldStrikes"/> with scripted storms
/// (top 18 km, precipitation 0.9, kind 4) against the real field profile: exactly zero strikes from storms whose centre is beyond 12 km of a wall (13, 20, 40, 80 and 159 km,
/// both walls), from a top under 15 km, a weak or immature kind 3, a non-tower, the megastorm kind and a null provider; the rate (one per 2-8 minutes per storm), the
/// strike-rate scale, the per-bucket cooldown, determinism (two runs, a split window, another seed), every strike's geometry (contact 0-100 m under H, channel 0.3-3 km, within
/// 12 km of the wall, inside the tower core), and the real <c>LightningDisturbanceAdapter</c> + <c>ContainmentDisturbanceBus</c> on a dense stream (one FieldStrike per event,
/// SourceId = EventId, natural source, at least 1 s between accepted spawns). Then a staged run through the real layer with a registered storm parked on the wall: the flagged
/// event appears for its own duration, equals the pure strike, the live host's adapter publishes it once, a rewind reproduces it, and none with a null provider or a weak storm.
/// Run: tools/natural_field_strike_test.gd. Prints NATURAL_FIELD_STRIKE=PASS.
/// </summary>
public partial class NaturalFieldStrikeTest : RefCounted
{
    readonly List<string> _failures = new();
    int _stage, _wait;
    void Check(bool ok, string message) { if (!ok) _failures.Add(message); }
    static void Info(string message) => Console.WriteLine("NATURAL_FIELD_STRIKE_INFO " + message);

    int Finish(string stage)
    {
        if (_failures.Count == 0) { Info(stage + " ok"); return 0; }
        foreach (string f in _failures) Console.Error.WriteLine("NATURAL_FIELD_STRIKE=FAIL " + stage + ": " + f);
        return 1;
    }

    const double W = 318_000.0, Around = 1_000_000.0, Hour = 3600.0;
    static readonly ContainmentFieldProfile Prof = new(W, 10_400.0, 40_000.0, -64.0, W + 64.0, 1_600.0);
    static NaturalFieldGeometry Field(bool nullProvider = false)
        => new(nullProvider ? null! : across => ContainmentFieldGeometry.HeightAt(Prof, across), Prof.RootAcross0, Prof.RootAcrossW, W, 31_415_936.0);

    static NaturalFieldStorm Storm(int id, double across, int kind = 4, float precip = 0.9f, float top = 18_000.0f, bool sculpt = true, bool mature = true, float major = 70_000.0f, float minor = 25_000.0f, double around = Around)
        => new(id, kind, around, across, major, minor, 1.0f, 0.0f, precip, top, sculpt, mature);

    /// <summary>The stream for <paramref name="keys"/> storms made by <paramref name="make"/>(key) over [t0, t1), by bucket, exactly as the layer asks (MayRoll first, then the storm).</summary>
    static List<NaturalFieldStrike> Run(int seed, int keys, Func<int, NaturalFieldStorm> make, double t0, double t1, float scale, NaturalFieldGeometry field)
    {
        var list = new List<NaturalFieldStrike>();
        for (long b = NaturalFieldStrikes.BucketOf(t0); b <= NaturalFieldStrikes.BucketOf(t1); b++)
            for (int k = 1; k <= keys; k++)
                if (NaturalFieldStrikes.MayRoll(seed, k, b, scale) && NaturalFieldStrikes.TryStrike(seed, k, field, make(k), b, scale, out NaturalFieldStrike s) && s.StartTimeSeconds >= t0 && s.StartTimeSeconds < t1) list.Add(s);
        return list;
    }

    sealed class RecordingSink : IContainmentDisturbanceSink
    {
        public readonly ContainmentDisturbanceBus Bus;
        public readonly List<(long Id, double Start, bool Natural, ContainmentDisturbanceKind Kind, ContainmentPublishResult Result)> Log = new();
        public RecordingSink(ContainmentDisturbanceBus bus) { Bus = bus; }
        public bool Publish(in ContainmentDisturbance d)
        {
            ContainmentPublishResult r = Bus.TryPublish(d);
            Log.Add((d.SourceId, d.StartTimeSeconds, d.NaturalSource, d.Kind, r));
            return r <= ContainmentPublishResult.FarList;
        }
    }

    public int RunPure()
    {
        NaturalFieldGeometry field = Field();
        const int Seed = 1337, Keys = 32;

        // ---- zero strikes from storms far from the walls, on both walls ----
        int far = 0;
        foreach (double d in new[] { 13_000.0, 20_000.0, 40_000.0, 80_000.0, 159_000.0 })
            foreach (double across in new[] { d, W - d })
            {
                int n = Run(Seed, Keys, k => Storm(k, across), 0.0, Hour, 1.0f, field).Count;
                far += n;
                Check(n == 0, $"storm {d / 1000:0} km from a wall (across {across / 1000:0.0}) made {n} strikes");
            }
        Info($"storms 13 / 20 / 40 / 80 / 159 km from either wall: {far} strikes in {Keys} storm-hours each");

        // ---- the near storm: rate, geometry, cooldown ----
        List<NaturalFieldStrike> near = Run(Seed, Keys, k => Storm(k, 7_000.0), 0.0, Hour, 1.0f, field);
        double perStormHour = near.Count / (double)Keys;
        Check(perStormHour >= 6.0 && perStormHour <= 32.0, $"a near-wall tower strikes {perStormHour:0.0} times per hour (doc: one per 2-8 minutes, 7.5-30)");
        Check(near.Count > 0, "no strike from a storm 7 km from the wall");
        Info($"near-wall tower (kind 4, precipitation 0.9, top 18 km, centre 7 km): {near.Count} strikes in {Keys} storm-hours = {perStormHour:0.0} per storm-hour = one per {Hour / Math.Max(perStormHour, 1e-9) / 60.0:0.0} min");
        var perBucket = new Dictionary<(int, long), int>();
        var ids = new HashSet<long>();
        int geometry = 0;
        foreach (NaturalFieldStrike s in near)
        {
            perBucket[(s.SystemId, s.Bucket)] = perBucket.GetValueOrDefault((s.SystemId, s.Bucket)) + 1;
            Check(ids.Add(s.Id) && NaturalFieldStrikes.IsNaturalFieldStrikeId(s.Id), "id not unique or not tagged");
            double h = ContainmentFieldGeometry.HeightAt(Prof, s.AcrossMeters), below = h - s.ContactAltitudeMeters, length = s.ContactAltitudeMeters - s.BaseAltitudeMeters;
            bool ok = below >= 0.0 && below <= 100.0 && s.GapMeters >= 300.0 - 1e-6 && s.GapMeters <= 3000.0 && length >= 300.0 && length <= 3100.0 && s.WallDistanceMeters <= 12_000.0
                && s.AcrossMeters >= 0.0 && s.AcrossMeters <= 12_064.0 && s.Energy >= 0.45f && s.Energy <= 1.0f && s.DurationSeconds >= 0.85f && s.DurationSeconds <= 1.85f
                && s.StartTimeSeconds >= s.Bucket * 20.0 && s.StartTimeSeconds < s.Bucket * 20.0 + 20.0 && s.Kind == 4 && Math.Abs(s.AroundMeters - Around) <= 70_000.0 * 0.6 + 1.0;
            if (!ok) { geometry++; if (geometry <= 3) Check(false, $"strike geometry: across {s.AcrossMeters:0} below {below:0.0} length {length:0} gap {s.GapMeters:0} wall {s.WallDistanceMeters:0} start {s.StartTimeSeconds:0.0} bucket {s.Bucket}"); }
        }
        Check(perBucket.Values.All(v => v == 1), "more than one strike per system per 20 s bucket");
        Check(near.Min(s => s.GapMeters) < 400.0 && near.Max(s => s.GapMeters) > 1000.0, $"gap range {near.Min(s => s.GapMeters):0}-{near.Max(s => s.GapMeters):0} m is not the doc's 0.3-3 km spread");
        Info($"{near.Count} strikes: contact 0-100 m under H, channel {near.Min(s => s.ContactAltitudeMeters - s.BaseAltitudeMeters):0}-{near.Max(s => s.ContactAltitudeMeters - s.BaseAltitudeMeters):0} m, gap {near.Min(s => s.GapMeters):0}-{near.Max(s => s.GapMeters):0} m, wall distance max {near.Max(s => s.WallDistanceMeters):0} m, at most 1 per system per bucket");

        // The far wall mirrors the near one.
        List<NaturalFieldStrike> mirror = Run(Seed, Keys, k => Storm(k, W - 7_000.0), 0.0, Hour, 1.0f, field);
        Check(mirror.Count > 0 && mirror.All(s => s.AcrossMeters >= W - 12_064.0 && s.WallDistanceMeters <= 12_000.0), $"the across-W wall: {mirror.Count} strikes");
        Check(Math.Abs(mirror.Count - near.Count) <= 0.45 * near.Count, $"the two walls differ: {near.Count} vs {mirror.Count}");

        // ---- gates ----
        int Count(Func<int, NaturalFieldStorm> make, NaturalFieldGeometry? f = null, float scale = 1.0f) => Run(Seed, Keys, make, 0.0, Hour, scale, f ?? field).Count;
        Check(Count(k => Storm(k, 7_000.0, top: 14_900.0f)) == 0, "a top under 15 km struck");
        Check(Count(k => Storm(k, 7_000.0, top: 15_000.0f)) > 0, "a 15 km top did not strike");
        Check(Count(k => Storm(k, 7_000.0, kind: 3, precip: 0.3f)) == 0, "a weak kind 3 (precipitation 0.3) struck");
        Check(Count(k => Storm(k, 7_000.0, kind: 3, precip: 0.9f, mature: false)) == 0, "an immature kind 3 struck");
        int kind3 = Count(k => Storm(k, 7_000.0, kind: 3, precip: 0.9f));
        Check(kind3 > 0 && kind3 < near.Count, $"a strong mature kind 3 made {kind3} against kind 4's {near.Count}");
        Check(Count(k => Storm(k, 7_000.0, kind: 4, precip: 0.3f, mature: false)) > 0, "an exceptional front under 0.6 precipitation made none (strength x 0.3 is allowed)");
        Check(Count(k => Storm(k, 7_000.0, sculpt: false)) == 0, "a non-tower struck");
        Check(Count(k => Storm(k, 7_000.0, kind: 6)) == 0, "the megastorm kind took the natural path");
        Check(Count(k => Storm(k, 7_000.0), Field(nullProvider: true)) == 0, "a null provider struck");
        Check(Run(Seed, Keys, k => Storm(k, 7_000.0), 0.0, Hour, 0.0f, field).Count == 0, "scale 0 struck");

        // ---- strike-rate scale (Lightning frequency x Reduced flashes) ----
        int half = Count(k => Storm(k, 7_000.0), null, 0.5f), twice = Count(k => Storm(k, 7_000.0), null, 2.0f);
        Check(half >= 0.35 * near.Count && half <= 0.65 * near.Count, $"scale 0.5 made {half} against {near.Count}");
        Check(twice >= 1.5 * near.Count && twice <= 2.2 * near.Count, $"scale 2 made {twice} against {near.Count}");
        Info($"strike-rate scale 0.5 / 1 / 2: {half} / {near.Count} / {twice}");

        // ---- determinism: two runs, a split window, another seed ----
        List<NaturalFieldStrike> again = Run(Seed, Keys, k => Storm(k, 7_000.0), 0.0, Hour, 1.0f, field);
        Check(again.SequenceEqual(near), "a second run differs");
        var split = Run(Seed, Keys, k => Storm(k, 7_000.0), 0.0, 1234.5, 1.0f, field);
        split.AddRange(Run(Seed, Keys, k => Storm(k, 7_000.0), 1234.5, Hour, 1.0f, field));
        Check(split.OrderBy(s => s.Id).SequenceEqual(near.OrderBy(s => s.Id)), "a split window differs from the whole one");
        Check(!Run(Seed + 1, Keys, k => Storm(k, 7_000.0), 0.0, Hour, 1.0f, field).SequenceEqual(near), "another seed gave the same strikes");

        // ---- the event convention ----
        NaturalFieldStrike probe = near[0];
        RingLightningEvent ev = NaturalFieldStrikes.ToEvent(probe);
        Check(ev.FieldStrike && ev.SystemKind == 4 && !ev.Scythe && !ev.IsPreview && ev.EventId == probe.Id && ev.SystemId == probe.SystemId && ev.LifeSeconds == probe.DurationSeconds && !ev.IsFieldArc,
            "event flag / kind / id / life");
        Check(ev.GroundPosition.VerticalMeters == probe.ContactAltitudeMeters && ev.CloudPosition.VerticalMeters == probe.BaseAltitudeMeters && ev.GroundPosition.AcrossMeters == probe.AcrossMeters, "event contact / base");
        Check(ev.PulseAt(probe.StartTimeSeconds + 0.5 * probe.DurationSeconds) > 0.0f && ev.PulseAt(probe.StartTimeSeconds + probe.DurationSeconds + 0.1) == 0.0f, "event pulse");

        // ---- the real adapter and bus: a dense stream (scale 2, 64 storms) ----
        {
            const int Dense = 64;
            List<NaturalFieldStrike> dense = Run(Seed, Dense, k => Storm(k, 7_000.0), 0.0, 600.0, 2.0f, field);
            var bus = new ContainmentDisturbanceBus(Prof, 31_415_936.0) { Observer = new RingCanonicalPosition(Around, 0.0, 6_000.0) };
            var sink = new RecordingSink(bus);
            var adapter = new LightningDisturbanceAdapter(sink, Prof, _ => null);
            var live = new List<RingLightningEvent>();
            var eventIds = dense.Select(s => s.Id).ToHashSet();
            var ripples = new HashSet<long>();
            for (double t = 0.0; t <= 625.0; t += 0.1)
            {
                live.Clear();
                foreach (NaturalFieldStrike s in dense) if (s.StartTimeSeconds <= t && t - s.StartTimeSeconds <= s.DurationSeconds) live.Add(NaturalFieldStrikes.ToEvent(s));
                bus.Advance(t);
                adapter.Update(live);
                foreach (ContainmentRipple? r in bus.Slots) if (r != null && r.Kind == ContainmentDisturbanceKind.FieldStrike) { ripples.Add(r.SourceId); Check(eventIds.Contains(r.SourceId), "a ripple whose SourceId is no event"); }
            }
            Check(sink.Log.Count == eventIds.Count && sink.Log.Select(l => l.Id).ToHashSet().SetEquals(eventIds), $"adapter published {sink.Log.Count} disturbances for {eventIds.Count} events");
            Check(sink.Log.All(l => l.Kind == ContainmentDisturbanceKind.FieldStrike && l.Natural), "a published disturbance is not a natural FieldStrike");
            var spawns = sink.Log.Where(l => l.Result == ContainmentPublishResult.Accepted).Select(l => l.Start).OrderBy(x => x).ToList();
            double minGap = double.MaxValue;
            for (int i = 1; i < spawns.Count; i++) minGap = Math.Min(minGap, spawns[i] - spawns[i - 1]);
            Check(spawns.Count > 3 && minGap >= 1.0 - 1e-9, $"natural spawn cap: {spawns.Count} spawns, closest pair {minGap:0.000} s");
            int limited = sink.Log.Count(l => l.Result == ContainmentPublishResult.RateLimited);
            Check(limited > 0, "the dense stream never reached the natural cap (test is vacuous)");
            Check(bus.PeakLive <= 16, "pool over 16");
            Info($"bus: {eventIds.Count} dense events (scale 2, 64 storms, 10 min) -> {spawns.Count} spawns, {limited} rate limited, {sink.Log.Count(l => l.Result == ContainmentPublishResult.Restruck)} restruck, "
                + $"{sink.Log.Count(l => l.Result == ContainmentPublishResult.Merged)} merged, {ripples.Count} distinct ripples seen, closest accepted pair {minGap:0.00} s");
        }
        return Finish("PURE");
    }

    // ---- Staged run through the real layer ----
    const int ManualId = 91001;
    List<NaturalFieldStrike> _targets = new();
    RingLightningEvent _first;
    int _targetIndex;
    NaturalFieldStrike? _naturalTarget;
    int _publishedBefore;
    Func<double, double>? _savedProvider;

    static RingLightningEvent? Find(RingVolumetricCloudLayer layer, long id)
    {
        foreach (RingLightningEvent e in layer.ActiveLightningEvents) if (e.EventId == id) return e;
        return null;
    }

    static RingVolumetricCloudLayer.LocalWeatherState Manual(double around, float precip, RingVolumetricCloudLayer.WeatherSystemKind kind)
        => new(ManualId, kind, 1.0f, 60_000.0f, new Vector2((float)around, 7_000.0f), new Vector2(60_000.0f, 25_000.0f), 0.0f, 1.0f, precip, Vector2.Zero, CloudFormationProfile.Cumulonimbus, 500.0f, 18_000.0f);

    public int Tick(Node layerNode)
    {
        var layer = (RingVolumetricCloudLayer)layerNode;
        try
        {
            if (_wait > 0) { _wait--; return -1; }
            var explorer = layer.GetNode<RingVoxelExplorerController>(layer.ExplorerPath);
            RingCanonicalPosition observer = explorer.CanonicalPosition;
            RingContainmentDisturbanceHost? host = RingContainmentDisturbanceHost.Instance;
            switch (_stage++)
            {
                case 0:
                {
                    Check(host?.Bus != null && ContainmentFieldProvider.IsInstalled && MegastormFieldCoupling.FieldUndersideProvider != null, "wiring: the real field provider / host is not installed");
                    if (host?.Bus == null || !ContainmentFieldProvider.IsInstalled) return Finish("LAYER");
                    _savedProvider = MegastormFieldCoupling.FieldUndersideProvider;
                    layer.CloudSimulationPaused = true;
                    layer.RegisterManualWeather("wo14", Manual(observer.AroundMeters, 0.9f, RingVolumetricCloudLayer.WeatherSystemKind.ExceptionalFront));
                    // The reference stream: the pure brute-force query over every cell that can reach a wall plus the registered storm.
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    List<NaturalFieldStrike> all = layer.QueryNaturalFieldStrikesInWindow(1000.0, 1000.0 + 2.0 * Hour, 1.0f);
                    double queryMs = watch.Elapsed.TotalMilliseconds;
                    List<NaturalFieldStrike> again = layer.QueryNaturalFieldStrikesInWindow(1000.0, 1000.0 + 2.0 * Hour, 1.0f);
                    Check(all.SequenceEqual(again), "the layer query is not deterministic");
                    _targets = all.Where(s => s.SystemId == ManualId).ToList();
                    var natural = all.Where(s => s.SystemId != ManualId).ToList();
                    Check(_targets.Count >= 10 && _targets.Count <= 8 * 30, $"the parked storm made {_targets.Count} strikes in 2 h");
                    var profile = host.Profile!.Value;
                    foreach (NaturalFieldStrike s in all)
                    {
                        double h = ContainmentFieldGeometry.HeightAt(profile, s.AcrossMeters);
                        Check(h - s.ContactAltitudeMeters >= 0.0 && h - s.ContactAltitudeMeters <= 100.0 && s.WallDistanceMeters <= 12_000.0, $"layer strike {s.Id} geometry (below H {h - s.ContactAltitudeMeters:0.0}, wall {s.WallDistanceMeters:0})");
                    }
                    Info($"layer query over 2 h of the whole ring: {all.Count} strikes ({_targets.Count} from the parked kind 4 storm, {natural.Count} from natural storms), {queryMs:0} ms for the first query");
                    _targets = _targets.Where((s, i) => i % Math.Max(1, _targets.Count / 4) == 0).Take(4).ToList();
                    Check(_targets.Count >= 3, "not enough targets to stage");
                    _targetIndex = 0;
                    return StartTarget(layer, host);
                }
                case 1:
                {
                    NaturalFieldStrike t = _targets[_targetIndex];
                    RingLightningEvent? e = Find(layer, t.Id);
                    Check(e.HasValue, $"strike {_targetIndex} did not appear in ActiveLightningEvents");
                    if (e is RingLightningEvent ev)
                    {
                        Check(ev == NaturalFieldStrikes.ToEvent(t), "the live event differs from the pure strike");
                        Check(ev.FieldStrike && ev.SystemKind == 4 && ev.LifeSeconds == t.DurationSeconds, "event flag / kind / life");
                        Check(ContainmentFieldProvider.Underside(ev.GroundPosition.AcrossMeters) - ev.GroundPosition.VerticalMeters is >= 0.0 and <= 100.0, "contact not within 100 m below the live H");
                        if (_targetIndex == 0) _first = ev;
                    }
                    Check(layer.ActiveLightningEvents.Count(x => x.FieldStrike && x.EventId == t.Id) == 1, "the strike is in the list more than once");
                    Check(host!.LightningAdapter!.StrikesPublished - _publishedBefore == 1, $"the adapter published {host.LightningAdapter.StrikesPublished - _publishedBefore} disturbances for one strike");
                    Check(host.Bus!.Slots.Any(r => r != null && r.SourceId == t.Id && r.Kind == ContainmentDisturbanceKind.FieldStrike), "no FieldStrike ripple with SourceId = EventId on the live bus");
                    // Mid-life, then just past its life.
                    layer.CloudSimulationTimeSeconds = t.StartTimeSeconds + t.DurationSeconds - 0.05;
                    _wait = 3; return -1;
                }
                case 2:
                {
                    NaturalFieldStrike t = _targets[_targetIndex];
                    Check(Find(layer, t.Id).HasValue, "the strike ended before its duration");
                    layer.CloudSimulationTimeSeconds = t.StartTimeSeconds + t.DurationSeconds + 0.2;
                    _wait = 3; return -1;
                }
                case 3:
                {
                    NaturalFieldStrike t = _targets[_targetIndex];
                    Check(!Find(layer, t.Id).HasValue, "the strike outlived its duration");
                    layer.CloudSimulationTimeSeconds = t.StartTimeSeconds - 0.3;
                    _wait = 3; return -1;
                }
                case 4:
                {
                    NaturalFieldStrike t = _targets[_targetIndex];
                    Check(!Find(layer, t.Id).HasValue, "the strike is live before its start");
                    if (++_targetIndex < _targets.Count) { _stage = 1; return StartTarget(layer, host!); }
                    // Rewind to the first strike: the identical event again.
                    layer.CloudSimulationTimeSeconds = _targets[0].StartTimeSeconds + 0.1;
                    _wait = 3; return -1;
                }
                case 5:
                {
                    RingLightningEvent? e = Find(layer, _targets[0].Id);
                    Check(e.HasValue && e.Value == _first, "a rewind did not reproduce the identical strike");
                    // A null provider: no strikes at the same moment (first past its life, so no earlier event lingers).
                    MegastormFieldCoupling.FieldUndersideProvider = null;
                    layer.CloudSimulationTimeSeconds = _targets[0].StartTimeSeconds + 5.0;
                    _wait = 3; return -1;
                }
                case 6:
                {
                    layer.CloudSimulationTimeSeconds = _targets[0].StartTimeSeconds + 0.2;
                    _wait = 3; return -1;
                }
                case 7:
                {
                    Check(layer.ActiveLightningEvents.All(x => !x.FieldStrike), "field strikes with a null provider");
                    MegastormFieldCoupling.FieldUndersideProvider = _savedProvider;
                    // A weak storm (kind 3, precipitation 0.3) at the same place.
                    layer.RegisterManualWeather("wo14", Manual(observer.AroundMeters, 0.3f, RingVolumetricCloudLayer.WeatherSystemKind.MajorFront));
                    layer.CloudSimulationTimeSeconds = _targets[0].StartTimeSeconds + 5.0;
                    _wait = 3; return -1;
                }
                case 8:
                {
                    layer.CloudSimulationTimeSeconds = _targets[0].StartTimeSeconds + 0.3;
                    _wait = 3; return -1;
                }
                case 9:
                {
                    Check(layer.ActiveLightningEvents.All(x => !x.FieldStrike), "a weak storm made a field strike");
                    List<NaturalFieldStrike> weak = layer.QueryNaturalFieldStrikesInWindow(1000.0, 1000.0 + 2.0 * Hour, 1.0f).Where(s => s.SystemId == ManualId).ToList();
                    Check(weak.Count == 0, $"the weak storm made {weak.Count} strikes in 2 h");
                    layer.UnregisterManualWeather("wo14");
                    layer.CloudSimulationTimeSeconds = _targets[0].StartTimeSeconds + 5.0;
                    _wait = 3; return -1;
                }
                case 10:
                {
                    layer.CloudSimulationTimeSeconds = _targets[0].StartTimeSeconds + 0.3;
                    _wait = 3; return -1;
                }
                case 11:
                {
                    Check(layer.ActiveLightningEvents.All(x => !x.FieldStrike || x.SystemId != ManualId), "a strike from an unregistered storm");
                    // A natural-cell strike through the live path (the lightning scan's candidate cache), against the brute-force query.
                    _naturalTarget = layer.QueryNaturalFieldStrikesInWindow(1000.0, 1000.0 + 2.0 * Hour, 1.0f).Where(s => s.SystemId != ManualId).Cast<NaturalFieldStrike?>().FirstOrDefault();
                    if (_naturalTarget is NaturalFieldStrike nt) { layer.CloudSimulationTimeSeconds = nt.StartTimeSeconds - 5.0; _wait = 3; return -1; }
                    Info("no natural-cell strike in the 2 h window to stage");
                    _stage = 14; return -1;
                }
                case 12:
                {
                    var nt = _naturalTarget!.Value;
                    var scan = (Action<double>)Delegate.CreateDelegate(typeof(Action<double>), layer,
                        typeof(RingVolumetricCloudLayer).GetMethod("ScanLightningCells", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!);
                    for (int i = 0; i < 700; i++) scan(nt.StartTimeSeconds - 5.0);
                    _publishedBefore = host!.LightningAdapter!.StrikesPublished;
                    layer.CloudSimulationTimeSeconds = nt.StartTimeSeconds + 0.1;
                    _wait = 4; return -1;
                }
                case 13:
                {
                    var nt = _naturalTarget!.Value;
                    RingLightningEvent? e = Find(layer, nt.Id);
                    Check(e.HasValue && e.Value == NaturalFieldStrikes.ToEvent(nt), $"the natural-cell strike {nt.Id} (system {nt.SystemId}, kind {nt.Kind}) did not reach the live list as the query says");
                    Info($"natural-cell strike through the live path: system {nt.SystemId} kind {nt.Kind} at t={nt.StartTimeSeconds:0.0}, across {nt.AcrossMeters:0}, gap {nt.GapMeters:0} m, live={e.HasValue}");
                    layer.CloudSimulationTimeSeconds = nt.StartTimeSeconds + 5.0;
                    _wait = 3; return -1;
                }
                case 14:
                {
                    // Per-frame cost through the layer's own method, with the live candidate cache: steady state, and across bucket changes (the memoised rebuild).
                    var call = (Action<double>)Delegate.CreateDelegate(typeof(Action<double>), layer,
                        typeof(RingVolumetricCloudLayer).GetMethod("TryScheduleNaturalFieldStrikes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!);
                    for (int i = 0; i < 300; i++) call(5000.0 + i / 30.0);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    const int Frames = 6000;
                    for (int i = 0; i < Frames; i++) call(5010.0 + i / 30.0);   // 200 s = 10 buckets
                    double micros = watch.Elapsed.TotalMilliseconds * 1000.0 / Frames;
                    // Fill the lightning candidate cache for real (the scan visits 64 cells a frame), then time cold buckets over it.
                    var scan = (Action<double>)Delegate.CreateDelegate(typeof(Action<double>), layer,
                        typeof(RingVolumetricCloudLayer).GetMethod("ScanLightningCells", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!);
                    for (int i = 0; i < 600; i++) scan(20_000.0);
                    int cells = ((System.Collections.ICollection)typeof(RingVolumetricCloudLayer).GetField("_lightningCandidateCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(layer)!).Count;
                    watch.Restart();
                    const int Buckets = 200;
                    for (int i = 0; i < Buckets; i++) call(20_000.0 + i * 20.0);   // a new bucket every call, every cell memo cold
                    double bucketMs = watch.Elapsed.TotalMilliseconds / Buckets;
                    Info($"layer scheduling {micros:F2} us per frame averaged over 10 buckets (budget 25 us); a cold bucket (rebuild over {cells} candidate cells) {bucketMs:F3} ms");
                    Check(micros < 25.0, $"layer scheduling costs {micros:F2} us per frame (budget 25)");
                    layer.CloudSimulationTimeSeconds = 0.0;
                    _wait = 2; return -1;
                }
            }
            return Finish("LAYER");
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); MegastormFieldCoupling.FieldUndersideProvider = _savedProvider ?? MegastormFieldCoupling.FieldUndersideProvider; return 1; }
        finally
        {
            if (_stage >= 15 || _failures.Count > 0) { if (_savedProvider != null) MegastormFieldCoupling.FieldUndersideProvider = _savedProvider; }
        }
    }

    int StartTarget(RingVolumetricCloudLayer layer, RingContainmentDisturbanceHost host)
    {
        _publishedBefore = host.LightningAdapter!.StrikesPublished;
        layer.CloudSimulationTimeSeconds = _targets[_targetIndex].StartTimeSeconds + 0.1;
        _wait = 4;
        return -1;
    }
}
