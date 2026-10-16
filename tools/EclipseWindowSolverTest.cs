using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Phase E0 of docs/eclipse-false-sun-spec.md (A.1-A.3, G.5): the pure whole-ring totality window solver. Centred totality length,
/// containment in every observer's own totality (360 longitudes x 3 across x 2 altitudes, checked by direct frame evaluation),
/// ring window length and umbra sweep, no-window cases, worker/main-thread equality and a golden (S, E) for eclipses 0-2.
/// </summary>
public partial class EclipseWindowSolverTest : RefCounted
{
    const string Format = "eclipse_window_golden_v1";
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static void Expect(List<string> failures, bool ok, string message) { if (!ok) failures.Add(message); }

    /// <param name="capture">Write the golden fixture (refuses to overwrite).</param>
    public int Run(bool capture)
    {
        var failures = new List<string>();
        try
        {
            var config = new CelestialConfiguration();
            var solver = new EclipseWindowSolver(config);
            double orbit = GasGiantConfiguration.DefaultOrbitSeconds;

            // Centred totality: 4,500 s centred on T * (k + 1/2).
            for (int k = 0; k < 4; k++)
            {
                EclipseWindow w = solver.Solve(k, 0d);
                Expect(failures, w.Valid, $"k={k}: window invalid");
                Expect(failures, Math.Abs(w.CentredDuration - 4500d) < 0.01, $"k={k}: centred duration {w.CentredDuration}");
                Expect(failures, Math.Abs((w.CentredStart + w.CentredEnd) * 0.5d - orbit * (k + 0.5d)) < 0.01, $"k={k}: centre {(w.CentredStart + w.CentredEnd) * 0.5d}");
                Expect(failures, Math.Abs(solver.CentreTime(k) - orbit * (k + 0.5d)) < 1e-9, $"k={k}: CentreTime");
                Console.WriteLine($"ECLIPSE_WINDOW k={k} centred=[{w.CentredStart:F2},{w.CentredEnd:F2}] ring_raw=[{w.RawRingStart:F2},{w.RawRingEnd:F2}] ring_len={w.RawRingDuration:F2} any=[{w.AnyStart:F2},{w.AnyEnd:F2}] any_len={w.AnyDuration:F2} sweep={w.SweepSeconds:F2}");
            }

            // Ring window length and sweep (spec A.1: 4,110 s whole-ring, 391 s spread, 4,893 s any-longitude).
            EclipseWindow w0 = solver.Solve(0, 0d);
            Expect(failures, Math.Abs(w0.RawRingDuration - 4110d) < 5d, $"ring window length {w0.RawRingDuration}");
            Expect(failures, Math.Abs(w0.SweepSeconds - 391d) < 8d, $"sweep {w0.SweepSeconds}");
            Expect(failures, Math.Abs(w0.AnyDuration - 4893d) < 10d, $"any-longitude length {w0.AnyDuration}");
            EclipseWindow wm = solver.Solve(0, 10d);
            Expect(failures, Math.Abs(wm.RingStart - (w0.RawRingStart + 10d)) < 1e-9 && Math.Abs(wm.RingEnd - (w0.RawRingEnd - 10d)) < 1e-9
                && Math.Abs(wm.RingDuration - (w0.RawRingDuration - 20d)) < 1e-9, "margin shrinks the window by 2 * margin");
            Expect(failures, Math.Abs(solver.Solve(0).MarginSeconds - 10d) < 1e-12, "default margin 10 s");

            // The ring window lies inside every observer's own totality: 360 longitudes x 3 across x 2 altitudes, direct evaluation.
            int observers = 0;
            foreach (int k in new[] { 0, 1 })
            {
                EclipseWindow w = solver.Solve(k);
                double[] samples = { w.RingStart, w.RingStart + 1d, (w.RingStart + w.RingEnd) * 0.5d, w.RingEnd - 1d, w.RingEnd };
                int bad = 0;
                for (int lon = 0; lon < 360; lon++)
                    foreach (double across in new[] { -159d, 0d, 159d })
                        foreach (double radius in new[] { 5000d, 4850d })
                        {
                            solver.GetObserver(lon * Math.PI / 180d, across, radius, out Vector3 up, out Vector3 offset);
                            observers++;
                            foreach (double t in samples) if (!solver.IsTotalAt(t, offset, up)) bad++;
                        }
                Expect(failures, bad == 0, $"k={k}: {bad} observer samples outside totality inside the ring window");
            }
            // The unshrunk bounds are tight: some longitude leaves totality just outside them.
            {
                int outsideStart = 0, outsideEnd = 0;
                for (int i = 0; i < 360; i++)
                {
                    solver.GetObserver(i * Math.PI / 180d, 0d, 5000d, out Vector3 up, out Vector3 offset);
                    if (!solver.IsTotalAt(w0.RawRingStart - 30d, offset, up)) outsideStart++;
                    if (!solver.IsTotalAt(w0.RawRingEnd + 30d, offset, up)) outsideEnd++;
                }
                Expect(failures, outsideStart > 0 && outsideEnd > 0, "ring window bounds are not tight");
            }

            // Local contacts bracket the ring window and sit within the parallax spread of the centred contacts.
            double worstOffset = 0d; int localOk = 0;
            for (int lon = 0; lon < 360; lon += 15)
            {
                EclipseLocalContacts lc = solver.LocalContacts(0, lon * Math.PI / 180d, 0d, 5000d);
                if (!lc.Valid) continue;
                localOk++;
                Expect(failures, lc.SecondContact <= w0.RawRingStart + 1e-6 && lc.ThirdContact >= w0.RawRingEnd - 1e-6, $"lon {lon}: local contacts do not contain the ring window");
                worstOffset = Math.Max(worstOffset, Math.Max(Math.Abs(lc.SecondContact - w0.CentredStart), Math.Abs(lc.ThirdContact - w0.CentredEnd)));
            }
            Expect(failures, localOk == 24 && worstOffset < 205d, $"local contacts ok={localOk} worst offset {worstOffset}");

            // No window: giant disabled, partial fixture, wide miss.
            var disabled = new CelestialConfiguration { GasGiant = new GasGiantConfiguration { Enabled = false } };
            Expect(failures, !new EclipseWindowSolver(disabled).Solve(0).Valid, "disabled giant produced a window");
            var partial = new CelestialConfiguration { GasGiant = new GasGiantConfiguration { GrazingMissDegrees = GasGiantConfiguration.PartialFixtureGrazingMissDegrees } };
            Expect(failures, !new EclipseWindowSolver(partial).Solve(0).Valid, "partial fixture (30.72 deg) produced a window");
            var wide = new CelestialConfiguration { GasGiant = new GasGiantConfiguration { GrazingMissDegrees = 45f } };
            Expect(failures, !new EclipseWindowSolver(wide).Solve(0).Valid, "wide miss produced a window");
            Expect(failures, !new EclipseWindowSolver(partial).LocalContacts(0, 0d, 0d, 5000d).Valid, "partial fixture gave local contacts");

            // Worker thread equals main thread, exactly.
            EclipseWindow main0 = solver.Solve(2), main1 = solver.Solve(1);
            EclipseWindow[] workers = await2(solver.SolveAsync(2), solver.SolveAsync(1));
            Expect(failures, workers[0] == main0 && workers[1] == main1, "worker result differs from main-thread result");
            Expect(failures, solver.Solve(2) == main0, "repeat solve differs");
            Expect(failures, solver.NearestIndex(orbit * 2.4d) == 2 && solver.NearestIndex(0d) == 0 && solver.NearestIndex(orbit * 1.4d) == 1, "NearestIndex");

            // Cost.
            solver.Solve(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) solver.Solve(i % 3);
            double solveMs = sw.Elapsed.TotalMilliseconds / 5d;

            Console.WriteLine($"ECLIPSE_WINDOW_SOLVER observers={observers} ring_raw_len={w0.RawRingDuration:F2} sweep={w0.SweepSeconds:F2} any_len={w0.AnyDuration:F2} local_worst_offset_s={worstOffset:F1} solve_ms={solveMs:F1}");

            // Golden.
            string fixture = ProjectSettings.GlobalizePath("res://tools/fixtures/eclipse_window_golden.json");
            var actual = new Golden { Format = Format, Windows = Enumerable.Range(0, 3).Select(k => Capture(solver.Solve(k))).ToList() };
            if (capture)
            {
                if (File.Exists(fixture)) { Console.Error.WriteLine("Refusing to overwrite existing golden fixture: " + fixture); return 1; }
                Directory.CreateDirectory(Path.GetDirectoryName(fixture)!);
                File.WriteAllText(fixture, JsonSerializer.Serialize(actual, JsonOptions) + System.Environment.NewLine);
                Console.WriteLine("ECLIPSE_WINDOW_GOLDEN captured " + fixture);
            }
            else if (!File.Exists(fixture)) failures.Add("golden fixture missing");
            else
            {
                Golden? expected = JsonSerializer.Deserialize<Golden>(File.ReadAllText(fixture), JsonOptions);
                if (expected == null || expected.Format != Format || expected.Windows.Count != actual.Windows.Count) failures.Add("golden unreadable");
                else
                    for (int i = 0; i < expected.Windows.Count; i++)
                    {
                        GoldenWindow e = expected.Windows[i], a = actual.Windows[i];
                        double worst = new[] { e.CentredStart - a.CentredStart, e.CentredEnd - a.CentredEnd, e.RawRingStart - a.RawRingStart, e.RawRingEnd - a.RawRingEnd,
                            e.RingStart - a.RingStart, e.RingEnd - a.RingEnd, e.AnyStart - a.AnyStart, e.AnyEnd - a.AnyEnd }.Max(Math.Abs);
                        Expect(failures, e.Index == a.Index && worst < 1e-3, $"golden k={e.Index} drift {worst} s");
                    }
            }
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("ECLIPSE_WINDOW_SOLVER=FAIL " + string.Join(" | ", failures.Take(30))); return 1; }
        Console.WriteLine("ECLIPSE_WINDOW_SOLVER=PASS");
        return 0;
    }

    static EclipseWindow[] await2(Task<EclipseWindow> a, Task<EclipseWindow> b) => new[] { a.GetAwaiter().GetResult(), b.GetAwaiter().GetResult() };

    static GoldenWindow Capture(EclipseWindow w) => new()
    {
        Index = w.Index, CentredStart = w.CentredStart, CentredEnd = w.CentredEnd, RawRingStart = w.RawRingStart, RawRingEnd = w.RawRingEnd,
        RingStart = w.RingStart, RingEnd = w.RingEnd, AnyStart = w.AnyStart, AnyEnd = w.AnyEnd
    };

    public sealed class Golden { public string Format { get; set; } = ""; public List<GoldenWindow> Windows { get; set; } = new(); }
    public sealed class GoldenWindow
    {
        public int Index { get; set; }
        public double CentredStart { get; set; }
        public double CentredEnd { get; set; }
        public double RawRingStart { get; set; }
        public double RawRingEnd { get; set; }
        public double RingStart { get; set; }
        public double RingEnd { get; set; }
        public double AnyStart { get; set; }
        public double AnyEnd { get; set; }
    }
}
