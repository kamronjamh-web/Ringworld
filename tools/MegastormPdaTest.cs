using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using RingworldTechDemo.Pda;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO6b (docs/megastorm-design.md 5.4, section 8): the megastorm PDA lines, headless with a fake clock and a capturing subtitle sink. A 350 m/s approach
/// from 2,500 km through the aftermath: the lines fire in the 5.4 order with the right facts; each id at most once per episode (rising at most twice, at
/// least 10 min apart); at most one non-critical line per 5 min; critical lines pre-empt; a resync speaks only the resync line; a second lap is a new
/// episode; the database unlock survives save and load. Also a 300 m/s approach (airborne line, no dead zone) and the hazard-driven suit warnings.
/// Run: Godot --headless --path . --script res://tools/megastorm_pda_test.gd. Prints MEGASTORM_PDA=PASS.
/// </summary>
public partial class MegastormPdaTest : RefCounted
{
    private const string PrefsPath = "res://.godot/megastorm-pda-test/preferences.tres";
    private const string LinesPath = "res://resources/pda/megastorm_lines.json", DatabasePath = "res://resources/pda/megastorm_database.json";
    private const double Around = 5.0e6, Across = 159000.0, StartDistance = 2.5e6, Circumference = 31415936.0;
    private sealed class Wrapper { public MegastormDocument? Megastorm { get; set; } }
    private sealed record Line(double T, string Id, int Priority, long Episode, string Text);

    private sealed class Capture : IPdaVoiceOutput
    {
        public PdaAnnouncer? Announcer;
        public MegastormPdaTriggers? Triggers;
        public double Now;
        public readonly List<Line> Lines = new();
        public int Subtitles;
        public void Speak(string line, int priority) => Lines.Add(new Line(Now, Announcer!.LastSpokenId ?? "", priority, Triggers?.Episode ?? 0, line));
        public void Subtitle(string line) => Subtitles++;
    }

    private sealed class Rig
    {
        public readonly RingMegastorm Node;
        public readonly Capture Out = new();
        public readonly PdaAnnouncer Announcer;
        public readonly MegastormPdaTriggers Triggers;
        public int Unlocks;
        public double Now;
        public double ArrivalSeconds;

        public Rig(Node host, PdaLineTable table, float speed)
        {
            Node = new RingMegastorm { ClockProvider = () => Now, MegastormPreferencesPath = PrefsPath, Name = "RingMegastormPdaTest" };
            host.AddChild(Node);
            ArrivalSeconds = StartDistance / speed;
            Node.Model.TriggerToReach(Around, Across, ArrivalSeconds, 1, "pda_test", 0.0, speed, 3, true);
            Announcer = new PdaAnnouncer(() => Now, null, Out);
            Triggers = new MegastormPdaTriggers(table, Announcer, () => Now, () => { Unlocks++; Node.ResonanceEntryUnlocked = true; });
            Out.Announcer = Announcer; Out.Triggers = Triggers;
        }

        public MegastormSample Sample(double t) => Node.Query(new RingCanonicalPosition(Around, 0.0, Across), t);

        public void Step(double t, MegastormPdaHazards? hazards = null)
        {
            Now = t; Out.Now = t;
            Triggers.Update(Sample(t), hazards);
            Announcer.Tick();
        }

        public void Run(double from, double to, double dt, MegastormPdaHazards? hazards = null)
        {
            for (double t = from; t <= to + 1e-9; t += dt) Step(t, hazards);
        }

        /// <summary>Runs with the hazard inputs taken from the model's own sample at every step (WO10), for an observer of the given shelter class.</summary>
        public void RunFromHazards(double from, double to, double dt, MegastormShelter shelter)
        {
            for (double t = from; t <= to + 1e-9; t += dt) Step(t, MegastormPdaHazards.FromSample(Sample(t), shelter));
        }
    }

    public int Run(Node host)
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProjectSettings.GlobalizePath(PrefsPath))!);
            if (File.Exists(ProjectSettings.GlobalizePath(PrefsPath))) File.Delete(ProjectSettings.GlobalizePath(PrefsPath));
            PdaLineTable table = PdaLineTable.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(LinesPath)));
            Table(table, Check);
            Announcer(table, Check);
            Approach(host, table, Check);
            Subsonic(host, table, Check);
            Resync(host, table, Check);
            Hazards(host, table, Check);
            ModelHazards(host, table, Check);
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); Console.Error.WriteLine("MEGASTORM_PDA=FAIL exception " + e.Message); return 1; }
        finally { try { File.Delete(ProjectSettings.GlobalizePath(PrefsPath)); } catch { } }
        if (failures.Count != 0) { Console.Error.WriteLine("MEGASTORM_PDA=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("MEGASTORM_PDA=PASS");
        return 0;
    }

    // ---- Authored table and database ----------------------------------------------------------------------------------------
    private static readonly string[] AllIds =
    {
        "megastorm_resonance_detected", "megastorm_resonance_rising", "megastorm_resonance_rising_far", "megastorm_fauna_silent", "megastorm_airborne_approach",
        "megastorm_acoustic_dead_zone", "megastorm_gust_warning", "megastorm_shelter_inadequate", "megastorm_wall_arrival", "megastorm_bullet_rain",
        "megastorm_wake_warning", "megastorm_scythe_warning", "megastorm_passage", "megastorm_all_clear", "megastorm_event_resync"
    };
    private static readonly HashSet<string> CriticalIds = new()
    {
        "megastorm_gust_warning", "megastorm_shelter_inadequate", "megastorm_wall_arrival", "megastorm_bullet_rain", "megastorm_wake_warning", "megastorm_scythe_warning"
    };

    private static void Table(PdaLineTable table, Action<bool, string> check)
    {
        foreach (string id in AllIds)
        {
            if (!table.TryGet(id, out PdaLineDefinition d)) { check(false, "table is missing " + id); continue; }
            check((d.Priority == 0) == CriticalIds.Contains(id), $"{id} critical flag / priority {d.Priority}");
            check(table.TryCreate(id, d.Facts, out PdaRequest r) && r.Critical == (d.Priority == 0), $"{id} request");
            string line = AuthoredPdaLineProvider.Fill(d.Fallback, d.Facts);
            check(!line.Contains('{') && !line.Contains('}'), $"{id} has unfilled placeholders: {line}");
        }
        check(table.Lines.Count == AllIds.Length, $"table has {table.Lines.Count} lines, expected {AllIds.Length}");
        PdaDatabaseEntry entry = PdaDatabaseEntry.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(DatabasePath)));
        check(entry.Id == "megastorm_structural_resonance" && entry.Title == "Structural resonance" && entry.UnlockedBy == "megastorm_resonance_detected" && entry.Paragraphs.Count >= 8, "database entry");
    }

    // ---- Announcer queue rules --------------------------------------------------------------------------------------------
    private static void Announcer(PdaLineTable table, Action<bool, string> check)
    {
        PdaRequest Make(string id, params (string, string)[] facts)
        {
            var d = new Dictionary<string, string>(); foreach (var (k, v) in facts) d[k] = v;
            table.TryCreate(id, d, out PdaRequest r); return r;
        }
        double now = 0.0;
        var output = new Capture();
        var a = new PdaAnnouncer(() => now, null, output);
        output.Announcer = a;
        // Priority order, 4 s cooldown, subtitles shown.
        a.Submit(Make("megastorm_fauna_silent", ("eta_minutes", "13")));
        a.Submit(Make("megastorm_resonance_detected", ("distance_km", "1500"), ("bearing", "spinward")));
        a.Tick();
        check(output.Lines.Count == 1 && output.Lines[0].Id == "megastorm_resonance_detected" && output.Subtitles == 1, "priority 2 did not speak before priority 3");
        now = 2.0; a.Tick();
        check(output.Lines.Count == 1, "second line spoke inside the 4 s cooldown");
        now = 4.0; a.Tick();
        check(output.Lines.Count == 2 && output.Lines[1].Id == "megastorm_fauna_silent" && output.Lines[1].Text.Contains("13 minutes"), "queued line did not speak after the cooldown");
        // 60 s per id.
        now = 10.0; a.Submit(Make("megastorm_fauna_silent", ("eta_minutes", "12"))); now = 20.0; a.Tick();
        check(output.Lines.Count == 2, "same id spoke again inside 60 s");
        // Stale requests are dropped.
        now = 100.0; a.Submit(Make("megastorm_passage", ("minutes", "16")));
        now = 100.0; a.Tick();
        check(output.Lines.Count == 3, "passage did not speak");
        now = 101.0; a.Submit(Make("megastorm_all_clear", ("distance_km", "390")));
        now = 110.0; a.Tick();
        check(output.Lines.Count == 3 && a.Pending == 0, "stale request was spoken or kept");
        // Critical pre-empts: speaks at once, inside the cooldown, and drops the queued non-critical backlog.
        now = 200.0; a.Submit(Make("megastorm_passage", ("minutes", "16"))); a.Tick();           // speaks (id cooled down)
        now = 201.0; a.Submit(Make("megastorm_all_clear", ("distance_km", "390")));
        a.Submit(Make("megastorm_gust_warning", ("wind_ms", "41")));
        check(output.Lines.Count == 5 && output.Lines[4].Id == "megastorm_gust_warning" && output.Lines[4].Priority == 0 && a.Pending == 0, "critical did not pre-empt the queue");
        // Resync: backlog dropped, only the resync line spoken.
        now = 400.0; a.Submit(Make("megastorm_all_clear", ("distance_km", "390"))); a.Submit(Make("megastorm_passage", ("minutes", "16")));
        a.SubmitResync(Make("megastorm_event_resync", ("phase", "trailing rain")));
        now = 420.0; a.Tick();
        check(output.Lines.Count == 6 && output.Lines[5].Id == "megastorm_event_resync" && output.Lines[5].Text.Contains("trailing rain") && a.Pending == 0, "resync did not speak alone");
        // Disabled: nothing.
        a.Enabled = false; now = 500.0; a.Submit(Make("megastorm_gust_warning", ("wind_ms", "41"))); a.Tick();
        check(output.Lines.Count == 6, "disabled announcer spoke");
    }

    // ---- 350 m/s approach, two laps, persistence -------------------------------------------------------------------------------
    private static readonly string[] ExpectedOrder =
    {
        "megastorm_resonance_detected", "megastorm_resonance_rising", "megastorm_resonance_rising", "megastorm_fauna_silent", "megastorm_acoustic_dead_zone",
        "megastorm_gust_warning", "megastorm_wall_arrival", "megastorm_wake_warning", "megastorm_passage", "megastorm_all_clear"
    };

    private static int FirstInt(string text, string pattern)
    {
        Match m = Regex.Match(text, pattern);
        return m.Success ? int.Parse(m.Groups[1].Value) : int.MinValue;
    }

    private static void Approach(Node host, PdaLineTable table, Action<bool, string> check)
    {
        var rig = new Rig(host, table, 350f);
        double ta = rig.ArrivalSeconds;
        double lap = Circumference / 350.0;
        // Lap 1 from 2,500 km through the aftermath, then skip to the next approach and run it again.
        rig.Run(0.0, ta + 3000.0, 1.0);
        long episode1 = rig.Triggers.Episode;
        double secondStart = ta + lap - 8000.0;
        rig.Run(ta + 3010.0, secondStart, 10.0);
        rig.Run(secondStart + 1.0, ta + lap + 3000.0, 1.0);
        long episode2 = rig.Triggers.Episode;
        check(episode1 != 0 && episode2 != 0 && episode1 != episode2, "second lap did not start a new episode");

        var first = rig.Out.Lines.Where(l => l.Episode == episode1).ToList();
        var second = rig.Out.Lines.Where(l => l.Episode == episode2).ToList();
        check(rig.Out.Lines.Count == first.Count + second.Count, "lines outside the two episodes");
        foreach (var (name, lines) in new[] { ("lap 1", first), ("lap 2", second) })
        {
            check(lines.Select(l => l.Id).SequenceEqual(ExpectedOrder), $"{name} order: {string.Join(",", lines.Select(l => l.Id.Replace("megastorm_", "")))}");
            foreach (var g in lines.GroupBy(l => l.Id)) check(g.Count() <= (g.Key == "megastorm_resonance_rising" ? 2 : 1), $"{name} {g.Key} spoke {g.Count()} times");
            var nonCritical = lines.Where(l => l.Priority != 0).ToList();
            for (int i = 1; i < nonCritical.Count; i++) check(nonCritical[i].T - nonCritical[i - 1].T >= 300.0, $"{name} non-critical lines {nonCritical[i - 1].Id}/{nonCritical[i].Id} only {nonCritical[i].T - nonCritical[i - 1].T:0} s apart");
            var rising = lines.Where(l => l.Id == "megastorm_resonance_rising").ToList();
            check(rising.Count == 2 && rising[1].T - rising[0].T >= 600.0, $"{name} rising lines not 10 min apart");
            check(lines.Where(l => CriticalIds.Contains(l.Id)).All(l => l.Priority == 0) && lines.Where(l => !CriticalIds.Contains(l.Id)).All(l => l.Priority != 0), $"{name} critical priorities");
            if (lines.Count != ExpectedOrder.Length) continue;
            // Facts and timing (T-0 is the edge reaching the observer at ta on the first lap, ta + lap on the second).
            double t0 = name == "lap 1" ? ta : ta + lap;
            Line L(string id, int n = 0) => lines.Where(l => l.Id == id).ElementAt(n);
            Line detected = L("megastorm_resonance_detected");
            int km = FirstInt(detected.Text, @"about (\d+) kilometres");
            check(km >= 1500 && km <= 1560 && detected.Text.Contains("antispinward"), $"{name} detected facts: {detected.Text}");
            check(Math.Abs((detected.T - t0) / 60.0 + 73.0) < 3.0, $"{name} detected at T{(detected.T - t0) / 60.0:0.0} min");
            Line r1 = L("megastorm_resonance_rising", 0), r2 = L("megastorm_resonance_rising", 1);
            check(Math.Abs(FirstInt(r1.Text, @"(\d+) kilometres out") - 1100) <= 12 && Math.Abs(FirstInt(r1.Text, @"in (\d+) minutes") - 52) <= 3, $"{name} rising 1 facts: {r1.Text}");
            check(Math.Abs(FirstInt(r2.Text, @"(\d+) kilometres out") - 788) <= 12 && Math.Abs(FirstInt(r2.Text, @"in (\d+) minutes") - 38) <= 3, $"{name} rising 2 facts: {r2.Text}");
            int eta = FirstInt(L("megastorm_fauna_silent").Text, @"in (\d+) minutes");
            check(eta >= 12 && eta <= 15, $"{name} fauna eta {eta}");
            int dead = FirstInt(L("megastorm_acoustic_dead_zone").Text, @"in (\d+) seconds");
            check(dead >= 188 && dead <= 196, $"{name} dead zone seconds {dead}");
            Line gust = L("megastorm_gust_warning");
            check(gust.Text.Contains("41 metres per second") && gust.T - t0 > -47.0 && gust.T - t0 < -43.0, $"{name} gust warning: {gust.Text} at {gust.T - t0:0.0}");
            Line wall = L("megastorm_wall_arrival");
            check(wall.Text.Contains("in 10 seconds") && wall.Text.Contains("60 kilopascals") && wall.T - t0 > -11.0 && wall.T - t0 < -8.0, $"{name} wall arrival: {wall.Text} at {wall.T - t0:0.0}");
            Line wake = L("megastorm_wake_warning");
            check(wake.Text.Contains("8 kilopascals") && Math.Abs(wake.T - t0 - 57.0) < 6.0, $"{name} wake warning: {wake.Text} at {wake.T - t0:0.0}");
            Line passage = L("megastorm_passage");
            check(passage.Text.Contains("16 minutes") && Math.Abs(passage.T - t0 - 143.0) < 8.0, $"{name} passage: {passage.Text} at {passage.T - t0:0.0}");
            Line clear = L("megastorm_all_clear");
            check(clear.Text.Contains("390 kilometres") && Math.Abs((clear.T - t0) / 60.0 - 18.6) < 0.5, $"{name} all clear: {clear.Text} at {(clear.T - t0) / 60.0:0.0} min");
        }
        check(rig.Out.Subtitles == rig.Out.Lines.Count, "subtitles are not shown for every line");

        // Sample audio fields come from the pure maths.
        MegastormSample s = rig.Sample(ta - 2000.0);
        MegastormModel model = rig.Node.Model;
        check(s.Active && Math.Abs(s.HumLevel - MegastormSound.HumLevel(s.XiMeters, model.Settings.WarningReachMeters, model.TrailingEndXi)) < 1e-6f && s.HumLevel > 0.4f
            && s.AirSound == AirSoundState.Silent && s.EpisodeId == rig.Sample(ta - 1000.0).EpisodeId, "MegastormSample audio fields");
        check(!rig.Node.Query(new RingCanonicalPosition(Around, 0.0, Across), -1000.0).Active, "sample active before the trigger");

        // Persistence: the database unlock survives save and load (and a node that never heard the hum stays locked).
        check(rig.Unlocks >= 1 && rig.Node.ResonanceEntryUnlocked, "database entry not unlocked by the detected line");
        string json = JsonSerializer.Serialize(new Wrapper { Megastorm = rig.Node.ToDocument() });
        var restored = new RingMegastorm { ClockProvider = () => rig.Now, MegastormPreferencesPath = PrefsPath, Name = "RingMegastormPdaRestored" };
        host.AddChild(restored);
        check(!restored.ResonanceEntryUnlocked, "fresh node starts unlocked");
        restored.RestoreFrom(JsonSerializer.Deserialize<Wrapper>(json)?.Megastorm);
        check(restored.Triggered && restored.ResonanceEntryUnlocked, "database unlock lost through save/load");
        rig.Node.ResonanceEntryUnlocked = false;
        restored.RestoreFrom(JsonSerializer.Deserialize<Wrapper>(JsonSerializer.Serialize(new Wrapper { Megastorm = rig.Node.ToDocument() }))?.Megastorm);
        check(restored.Triggered && !restored.ResonanceEntryUnlocked, "locked entry loaded unlocked");
        restored.QueueFree(); rig.Node.QueueFree();
    }

    // ---- 300 m/s: the airborne line and no dead zone ---------------------------------------------------------------------------
    private static void Subsonic(Node host, PdaLineTable table, Action<bool, string> check)
    {
        var rig = new Rig(host, table, 300f);
        rig.Run(0.0, rig.ArrivalSeconds + 3000.0, 1.0);
        string[] ids = rig.Out.Lines.Select(l => l.Id).ToArray();
        string[] expected = ExpectedOrder.Select(i => i == "megastorm_acoustic_dead_zone" ? "megastorm_airborne_approach" : i).ToArray();
        check(ids.SequenceEqual(expected), "300 m/s order: " + string.Join(",", ids.Select(i => i.Replace("megastorm_", ""))));
        Line? air = rig.Out.Lines.FirstOrDefault(l => l.Id == "megastorm_airborne_approach");
        check(air != null && FirstInt(air.Text, @"detected, (\d+) kilometres") is >= 70 and <= 80, "airborne approach facts: " + air?.Text);
        rig.Node.QueueFree();
    }

    // ---- Resync: load or jump into an episode ---------------------------------------------------------------------------------
    private static readonly string[] AfterResync =
    {
        "megastorm_event_resync", "megastorm_fauna_silent", "megastorm_acoustic_dead_zone", "megastorm_gust_warning", "megastorm_wall_arrival",
        "megastorm_wake_warning", "megastorm_passage", "megastorm_all_clear"
    };

    private static void Resync(Node host, PdaLineTable table, Action<bool, string> check)
    {
        // Loading straight into the episode at 700 km: only the resync line, then the stages still to come.
        var load = new Rig(host, table, 350f);
        double ta = load.ArrivalSeconds, at700 = ta - 700000.0 / 350.0;
        load.Step(at700);
        check(load.Out.Lines.Count == 1 && load.Out.Lines[0].Id == "megastorm_event_resync" && load.Out.Lines[0].Text.Contains("approach"), "load did not speak only the resync line");
        check(load.Unlocks >= 1 && load.Node.ResonanceEntryUnlocked, "resync did not unlock the database entry");
        load.Run(at700 + 1.0, ta + 3000.0, 1.0);
        check(load.Out.Lines.Select(l => l.Id).SequenceEqual(AfterResync), "load order: " + string.Join(",", load.Out.Lines.Select(l => l.Id.Replace("megastorm_", ""))));
        load.Node.QueueFree();

        // A jump forward during normal play: quiet before (hum below the detect level), then resync only, no backlog.
        var jump = new Rig(host, table, 350f);
        jump.Run(2500.0, 2600.0, 1.0);
        check(jump.Out.Lines.Count == 0, "line spoken before the hum was audible");
        jump.Run(at700, at700 + 20.0, 1.0);
        check(jump.Out.Lines.Count == 1 && jump.Out.Lines[0].Id == "megastorm_event_resync", "jump did not speak only the resync line");
        jump.Run(at700 + 21.0, ta + 3000.0, 1.0);
        check(jump.Out.Lines.Select(l => l.Id).SequenceEqual(AfterResync), "jump order: " + string.Join(",", jump.Out.Lines.Select(l => l.Id.Replace("megastorm_", ""))));
        jump.Node.QueueFree();

        // Loading into an aftermath episode resyncs too; loading after the aftermath stays silent.
        var late = new Rig(host, table, 350f);
        late.Step(ta + 1500.0);
        check(late.Out.Lines.Count == 1 && late.Out.Lines[0].Id == "megastorm_event_resync" && late.Out.Lines[0].Text.Contains("clearing"), "aftermath load");
        late.Run(ta + 1501.0, ta + 3000.0, 1.0);
        check(late.Out.Lines.Count == 1, "aftermath load replayed lines");
        late.Node.QueueFree();
        var gone = new Rig(host, table, 350f);
        gone.Step(ta + 6000.0);
        check(gone.Out.Lines.Count == 0, "load long after the storm spoke");
        gone.Node.QueueFree();
    }

    // ---- Hazard-driven suit warnings (shelter, bullet rain, scythes) ----------------------------------------------------------------
    private static void Hazards(Node host, PdaLineTable table, Action<bool, string> check)
    {
        var rig = new Rig(host, table, 350f);
        double ta = rig.ArrivalSeconds;
        var hazards = new MegastormPdaHazards(ShelterOpen: true, RainImpactKilowattsPerSquareMeter: 3.0f, Exposure: 0.5f, ScytheWithin5Km: true);
        rig.Run(ta - 120.0, ta + 2000.0, 1.0, hazards);
        string[] expected = { "megastorm_event_resync", "megastorm_shelter_inadequate", "megastorm_gust_warning", "megastorm_wall_arrival", "megastorm_bullet_rain",
            "megastorm_scythe_warning", "megastorm_wake_warning", "megastorm_passage", "megastorm_all_clear" };
        check(rig.Out.Lines.Select(l => l.Id).SequenceEqual(expected), "hazard order: " + string.Join(",", rig.Out.Lines.Select(l => l.Id.Replace("megastorm_", ""))));
        Line? shelter = rig.Out.Lines.FirstOrDefault(l => l.Id == "megastorm_shelter_inadequate");
        check(shelter != null && shelter.Text.Contains("60 kilopascals") && Math.Abs(shelter.T - (ta - 60.0)) < 2.0, "shelter line: " + shelter?.Text);
        Line? bullet = rig.Out.Lines.FirstOrDefault(l => l.Id == "megastorm_bullet_rain");
        check(bullet != null && bullet.Text.Contains("140 metres per second"), "bullet rain line: " + bullet?.Text);
        // The non-critical passage line waited out the 5 min spacing after the resync, then still spoke.
        Line? passage = rig.Out.Lines.FirstOrDefault(l => l.Id == "megastorm_passage");
        check(passage != null && passage.T - (ta - 120.0) >= 300.0, "passage line ignored the 5 min spacing after the resync");
        rig.Node.QueueFree();
    }

    // ---- Hazard lines fed by the WO10 hazard model (shelter class from the observer, kpa and speed from the sample) ----------------------------------
    private static void ModelHazards(Node host, PdaLineTable table, Action<bool, string> check)
    {
        var open = new Rig(host, table, 350f);
        double ta = open.ArrivalSeconds;
        open.RunFromHazards(ta - 120.0, ta + 2000.0, 1.0, MegastormShelter.Open);
        string[] ids = open.Out.Lines.Select(l => l.Id).ToArray();
        int Index(string id) => Array.IndexOf(ids, id);
        string order = string.Join(",", ids.Select(i => i.Replace("megastorm_", "")));
        check(Index("megastorm_shelter_inadequate") >= 0 && Index("megastorm_shelter_inadequate") < Index("megastorm_wall_arrival") && Index("megastorm_wall_arrival") < Index("megastorm_bullet_rain")
            && Index("megastorm_bullet_rain") < Index("megastorm_wake_warning") && Index("megastorm_scythe_warning") > Index("megastorm_wall_arrival"), "model hazard order: " + order);
        MegastormSample ahead = open.Sample(ta - 30.0);
        Line? shelter = open.Out.Lines.FirstOrDefault(l => l.Id == "megastorm_shelter_inadequate");
        check(shelter != null && Math.Abs(shelter.T - (ta - 60.0)) < 2.0 && ahead.Hazards.BoomPeakKPa > 50.0f, "model shelter line: " + shelter?.Text);
        Line? bullet = open.Out.Lines.FirstOrDefault(l => l.Id == "megastorm_bullet_rain");
        var speedMatch = bullet == null ? null : System.Text.RegularExpressions.Regex.Match(bullet.Text, @"velocity (\d+) metres per second");
        check(bullet != null && speedMatch!.Success && int.Parse(speedMatch.Groups[1].Value) is >= 80 and <= 141 && bullet.T > ta && bullet.T < ta + 10.0, "model bullet rain line (the speed at the 2 kW/m2 onset): " + bullet?.Text);
        Line? wake = open.Out.Lines.FirstOrDefault(l => l.Id == "megastorm_wake_warning");
        check(wake != null && wake.Text.Contains("8 kilopascals") && wake.T > ta + 55.0 && wake.T < ta + 60.0, "model wake line: " + wake?.Text);
        open.Node.QueueFree();

        // Lee and enclosed observers get no shelter-inadequate line and an attenuated figure in the arrival line.
        foreach ((MegastormShelter shelterClass, float factor) in new[] { (MegastormShelter.Lee, 0.6f), (MegastormShelter.Enclosed, 0.25f) })
        {
            var rig = new Rig(host, table, 350f);
            rig.RunFromHazards(rig.ArrivalSeconds - 120.0, rig.ArrivalSeconds + 60.0, 1.0, shelterClass);
            check(rig.Out.Lines.All(l => l.Id != "megastorm_shelter_inadequate"), $"{shelterClass}: shelter-inadequate line spoke");
            Line? arrival = rig.Out.Lines.FirstOrDefault(l => l.Id == "megastorm_wall_arrival");
            float expected = rig.Sample(rig.ArrivalSeconds - 5.0).Hazards.BoomPeakKPa * factor;
            check(arrival != null && arrival.Text.Contains(expected.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)), $"{shelterClass}: arrival line {arrival?.Text} vs {expected}");
            rig.Node.QueueFree();
        }

        // A 300 m/s storm has no boom: no shelter line, and the figure reads 0.
        var slow = new Rig(host, table, 300f);
        slow.RunFromHazards(slow.ArrivalSeconds - 120.0, slow.ArrivalSeconds + 60.0, 1.0, MegastormShelter.Open);
        check(slow.Out.Lines.All(l => l.Id != "megastorm_shelter_inadequate"), "subsonic: shelter-inadequate line spoke");
        slow.Node.QueueFree();
    }
}
