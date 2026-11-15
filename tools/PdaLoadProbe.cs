using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Godot;
using RingworldTechDemo.Pda;

namespace RingworldTechDemo.Tools;

/// <summary>
/// [latency instrumentation] Manual measurement aid, not a test. Logs per-second frame times of a running game scene,
/// dumps live Focused PDA contexts (timed exactly like the F10 panel) and captures one current-view PNG with the
/// panel's capture path. Writes only below res://.godot/pda_load/. Stop by creating .godot/pda_load/stop.
/// </summary>
public partial class PdaLoadProbe : Node
{
    private readonly List<double> _frames = new();
    private StreamWriter? _csv;
    private double _windowStart;
    private string _directory = "";
    public bool StopRequested { get; private set; }
    public string LastResult { get; private set; } = "";

    public void Begin(string directory)
    {
        _directory = ProjectSettings.GlobalizePath(directory);
        Directory.CreateDirectory(_directory);
        _csv = new StreamWriter(Path.Combine(_directory, "frames.csv"), append: true) { AutoFlush = true };
        _csv.WriteLine("unix_ms,frames,mean_ms,p50_ms,p95_ms,max_ms,fps");
        _windowStart = Time.GetTicksUsec() / 1e6;
    }

    public override void _Process(double delta)
    {
        if (_csv == null) return;
        _frames.Add(delta * 1000d);
        double now = Time.GetTicksUsec() / 1e6;
        if (now - _windowStart < 1d) return;
        double[] sorted = _frames.OrderBy(v => v).ToArray();
        double P(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(q * (sorted.Length - 1) + 0.5))];
        _csv.WriteLine(string.Join(",", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), sorted.Length,
            sorted.Average().ToString("0.00"), P(.5).ToString("0.00"), P(.95).ToString("0.00"), sorted[^1].ToString("0.00"),
            (sorted.Length / (now - _windowStart)).ToString("0.0")));
        _frames.Clear();
        _windowStart = now;
        if (File.Exists(Path.Combine(_directory, "stop"))) StopRequested = true;
        // Measurement hook: a "cap" file holding an integer emulates PdaDebugChatPanel.GenerationFrameCap while a replay runs.
        string capFile = Path.Combine(_directory, "cap");
        int cap = File.Exists(capFile) && int.TryParse(File.ReadAllText(capFile).Trim(), out int parsed) ? parsed : 0;
        if (Engine.MaxFps != cap) Engine.MaxFps = cap;
    }

    public override void _ExitTree() { _csv?.Dispose(); _csv = null; }

    /// <summary>One fresh snapshot + policy check + Focused composition per question, the panel's main-thread work.</summary>
    public string DumpContexts(Node world, string questionsPath, string outName)
    {
        try
        {
            var feed = world.GetNode<PdaContextFeed>("PdaContextFeed");
            string[] questions = JsonSerializer.Deserialize<string[]>(File.ReadAllText(ProjectSettings.GlobalizePath(questionsPath)))!;
            var items = new List<object>();
            int index = 0;
            foreach (string question in questions)
            {
                var watch = Stopwatch.StartNew();
                PdaContextSnapshot snapshot = feed.Capture();
                double captureMs = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                bool policy = PdaAnswerPolicy.TryAnswer(question, snapshot, out string policyAnswer, out string policyReason);
                double policyMs = watch.Elapsed.TotalMilliseconds;
                bool policyImage = PdaAnswerPolicy.TryAnswer(question, snapshot, out _, out _, imageAttached: true);
                watch.Restart();
                PdaQuestionContext text = PdaContextComposer.ComposeFocused(snapshot, question, Array.Empty<PdaConversationTurn>(), 6000);
                double composeMs = watch.Elapsed.TotalMilliseconds;
                PdaQuestionContext image = PdaContextComposer.ComposeFocused(snapshot, question, Array.Empty<PdaConversationTurn>(), 5600);
                string visual = $"\nVISUAL OBSERVATION (untrusted appearance, not a game fact): current camera PNG captured {DateTimeOffset.UtcNow:O}; paired snapshot {snapshot.Version}. Use confirmed game readings for hidden state and causes; image appearance can be ambiguous.\n";
                items.Add(new
                {
                    id = "live_" + (index++).ToString("00"), question, policy, policyImage, policyReason = policy ? policyReason : null, policyAnswer = policy ? policyAnswer : null,
                    snapshotVersion = snapshot.Version, captureMs, policyMs, composeMs,
                    context = PdaAnswerPolicy.ComposeModelContext(text.ContextText, question, snapshot),
                    imageContext = PdaAnswerPolicy.ComposeModelContext(image.ContextText, question, snapshot) + visual, contextRaw = text.ContextText, diagnostics = text.DebugText
                });
            }
            string path = Path.Combine(_directory, outName);
            File.WriteAllText(path, JsonSerializer.Serialize(new { capturedAtUtc = DateTimeOffset.UtcNow, items }, new JsonSerializerOptions { WriteIndented = true }));
            LastResult = "OK " + path;
        }
        catch (Exception error) { LastResult = "FAIL " + error; }
        return LastResult;
    }

    /// <summary>The panel's PdaCameraCapture path with a throwaway overlay; result in LastResult.</summary>
    public async void CaptureView(string outName)
    {
        LastResult = "";
        var overlay = new Control();
        AddChild(overlay);
        try
        {
            var watch = Stopwatch.StartNew();
            PdaCapturedView view = await PdaCameraCapture.CaptureAsync(this, overlay, CancellationToken.None);
            double ms = watch.Elapsed.TotalMilliseconds;
            string path = Path.Combine(_directory, outName);
            File.WriteAllBytes(path, view.PngBytes);
            LastResult = $"OK {path} captureMs={ms:0.0} bytes={view.PngBytes.Length}";
        }
        catch (Exception error) { LastResult = "FAIL " + error; }
        finally { overlay.QueueFree(); }
    }
}
