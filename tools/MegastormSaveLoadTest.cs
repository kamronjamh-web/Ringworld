using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO2 (docs/megastorm-design.md section 8): megastorm save/load. Save at T, run to T+600; reload, run to T+600; s, the bundle records and the
/// flash-slot ids must be identical (exact). An old save (no Megastorm member) and an invalid document load untriggered; a stopped storm (NaN stop
/// time in the model) survives System.Text.Json default options; a live menu speed change re-anchors continuously; the debug env parser.
/// Run: Godot --headless --path . --script res://tools/megastorm_save_load_test.gd. Prints MEGASTORM_SAVELOAD=PASS.
/// </summary>
public partial class MegastormSaveLoadTest : RefCounted
{
    private const string PrefsPath = "res://.godot/megastorm-saveload-test/preferences.tres";
    private sealed class Wrapper { public MegastormDocument? Megastorm { get; set; } }

    public int Run(Node host)
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProjectSettings.GlobalizePath(PrefsPath))!);
            if (File.Exists(ProjectSettings.GlobalizePath(PrefsPath))) File.Delete(ProjectSettings.GlobalizePath(PrefsPath));
            double clockA = 0.0, clockB = 0.0;
            RingMegastorm a = Spawn(host, () => clockA);
            RingMegastorm b = Spawn(host, () => clockB);
            Check(!a.Triggered && !a.Query(new RingCanonicalPosition(0, 0, 1000), 100.0).Active, "fresh node not untriggered");

            // Story trigger at t=1000, a speed change at 1300 (two anchors), save at T=2000.
            clockA = 1000.0;
            Check(a.Trigger(new MegastormTrigger("story_1", 5.0e6, 1, 1000.0, float.NaN, 1800f, 77)), "trigger refused");
            Check(!a.Trigger(new MegastormTrigger("story_2", 1.0e6, 1, 1000.0)), "second trigger accepted");
            clockA = 1300.0; a.SetSpeed(200f);
            const double T = 2000.0;
            clockA = T;
            string json = JsonSerializer.Serialize(new Wrapper { Megastorm = a.ToDocument() }, new JsonSerializerOptions { WriteIndented = true });
            Check(json.Contains("story_1"), "document missing trigger id");

            var traceA = Replay(a, T, T + 600.0, t => clockA = t);
            var wrapper = JsonSerializer.Deserialize<Wrapper>(json);
            b.RestoreFrom(wrapper?.Megastorm);
            clockB = T;
            Check(b.Triggered, "reload lost the trigger");
            var traceB = Replay(b, T, T + 600.0, t => clockB = t);
            Check(traceA.S.SequenceEqual(traceB.S), "s differs after reload");
            Check(traceA.Records.SequenceEqual(traceB.Records), "records differ after reload");
            Check(traceA.Lightning.SequenceEqual(traceB.Lightning), "lightning ids differ after reload");
            Check(traceA.Samples.SequenceEqual(traceB.Samples), "point samples / episode ids differ after reload");
            Check(traceA.Lightning.Count > 0, "no lightning ids produced (test is vacuous)");
            Check(traceA.Records.Count > 0 && traceA.S.Count > 0, "empty trace");
            Check(b.ToDocument()!.Anchors.Count == 2 && Math.Abs(b.ToDocument()!.Anchors[1].Speed - 200f) < 1e-6f, "anchors not restored");

            // Old save: no Megastorm member.
            var old = JsonSerializer.Deserialize<Wrapper>("{}");
            Check(old != null && old.Megastorm == null, "old document has a Megastorm member");
            b.RestoreFrom(old?.Megastorm);
            Check(!b.Triggered && b.ToDocument() == null && !b.Query(new RingCanonicalPosition(5.0e6, 0, 1000), 2500.0).Active, "old save did not load untriggered");
            // Invalid or newer documents are treated as untriggered, not as a crash.
            b.RestoreFrom(new MegastormDocument { Version = 99, TriggerId = "x", Anchors = { new MegastormAnchor { Speed = 100f } } });
            Check(!b.Triggered, "unknown document version accepted");
            b.RestoreFrom(new MegastormDocument { TriggerId = "x" });
            Check(!b.Triggered, "document without anchors accepted");

            // A stopped storm: the model NaN stop time must round-trip through default System.Text.Json options.
            clockA = T; a.Stop(T + 100.0, 60f);
            string stopJson = JsonSerializer.Serialize(new Wrapper { Megastorm = a.ToDocument() });
            b.RestoreFrom(JsonSerializer.Deserialize<Wrapper>(stopJson)?.Megastorm);
            Check(b.Model.State.StopTimeSeconds == T + 100.0 && b.Model.State.StopFadeSeconds == 60.0, "stop not restored");
            Check(!b.Model.Evaluate(T + 200.0).Active && b.Model.Evaluate(T + 90.0).Active, "stop fade not applied after reload");
            var unstopped = new MegastormDocument { TriggerId = "u", Anchors = { new MegastormAnchor { Speed = 100f } } };
            Check(double.IsNaN(unstopped.ToState()!.StopTimeSeconds), "null stop time did not map to NaN");

            // Live menu change: speed re-anchors at now, the position stays continuous, lengths replace the snapshot.
            clockB = 3000.0;
            b.RestoreFrom(JsonSerializer.Deserialize<Wrapper>(json)?.Megastorm);
            double before = b.Model.AroundAt(clockB);
            RingMegastormSettings.Current.SpeedMetersPerSecond = 100f;
            RingMegastormSettings.Current.TrailingLengthKm = 200f;
            b.ApplySettings();
            Check(Math.Abs(b.Model.AroundAt(clockB) - before) < 1e-3, "menu speed change jumped the position");
            Check(b.Model.SpeedAt(clockB) == 100f && b.Model.SpeedAt(clockB - 1.0) == 200f, "menu speed change not anchored at now");
            Check(b.Model.Settings.TrailingLengthMeters == 200000.0, "trailing length not applied");
            RingMegastormSettings.Current.SpeedMetersPerSecond = 350f; RingMegastormSettings.Current.TrailingLengthKm = 340f;
            b.ApplySettings();
            Check(b.Model.SpeedAt(clockB) == 350f, "menu speed change back not applied");

            // Debug environment parser.
            Check(RingMegastorm.TryParseDebugTrigger("1500,-1,120,200", out var dbg) && dbg.Around == 1.5e6 && dbg.Direction == -1 && dbg.Age == 120.0 && dbg.Speed == 200f, "debug trigger parse");
            Check(RingMegastorm.TryParseDebugTrigger("10,1,0", out dbg) && float.IsNaN(dbg.Speed), "debug trigger parse without speed");
            Check(!RingMegastorm.TryParseDebugTrigger("a,b", out _) && !RingMegastorm.TryParseDebugTrigger("", out _) && !RingMegastorm.TryParseDebugTrigger("1,1,x", out _), "debug trigger parse accepted garbage");

            a.QueueFree(); b.QueueFree();
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); Console.Error.WriteLine("MEGASTORM_SAVELOAD=FAIL exception " + e.Message); return 1; }
        finally { try { File.Delete(ProjectSettings.GlobalizePath(PrefsPath)); } catch { } }
        if (failures.Count != 0) { Console.Error.WriteLine("MEGASTORM_SAVELOAD=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("MEGASTORM_SAVELOAD=PASS");
        return 0;
    }

    private static RingMegastorm Spawn(Node host, Func<double> clock)
    {
        var node = new RingMegastorm { ClockProvider = clock, MegastormPreferencesPath = PrefsPath, Name = "RingMegastormTest" };
        host.AddChild(node);
        return node;
    }

    private sealed record Trace(List<double> S, List<double> Records, List<long> Lightning, List<double> Samples);

    private static Trace Replay(RingMegastorm node, double t0, double t1, Action<double> setClock)
    {
        var trace = new Trace(new(), new(), new(), new());
        var bundle = new List<MegastormRecord>();
        var ids = new List<long>();
        var probe = new RingCanonicalPosition(5.0e6 + 160000.0, 0.0, 120000.0);
        for (double t = t0; t <= t1; t += 1.0)
        {
            setClock(t);
            trace.S.Add(node.Model.AroundAt(t));
            if (((int)(t - t0)) % 10 == 0)
            {
                node.Model.BuildBundle(t, 100000.0, bundle);
                foreach (MegastormRecord r in bundle)
                {
                    trace.Records.Add(r.Id); trace.Records.Add(r.CenterAroundMeters); trace.Records.Add(r.CenterAcrossMeters);
                    trace.Records.Add(r.SemiAlongMeters); trace.Records.Add(r.Density); trace.Records.Add(r.Precipitation); trace.Records.Add(r.DriftMetersPerSecond);
                }
                MegastormSample s = node.QueryNow(probe);
                trace.Samples.Add(s.XiMeters); trace.Samples.Add(s.TimeToArrivalSeconds); trace.Samples.Add((int)s.Phase); trace.Samples.Add(s.EpisodeId); trace.Samples.Add(s.Danger);
            }
            if (t > t0) { MegastormLightning.FlashIdsInWindow(node.Model, t - 1.0, t, ids); trace.Lightning.AddRange(ids); }
        }
        return trace;
    }
}
