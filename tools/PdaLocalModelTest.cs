using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Pda;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Opt-in local model transport/lifecycle probe, not a semantic-quality gate. Requires the built Release helper and its local GGUF.
/// Run through tools/run_tests.ps1 -Tests pda_local_model_test -Label pda_debug -Cache reuse.
/// Prints each supplied context, answer and latency for review, then PDA_LOCAL_MODEL=PASS for lifecycle assertions only.
/// </summary>
public partial class PdaLocalModelTest : Node
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<object> _evidence = new();
    private LocalPdaQaClient? _client;
    private Task<string?>? _checks;
    public string Result { get; private set; } = "RUNNING";

    public override void _Ready()
    {
        string projectRoot = ProjectSettings.GlobalizePath("res://");
        _lifetime.CancelAfter(TimeSpan.FromSeconds(110));
        _checks = RunChecksAsync(projectRoot, _lifetime.Token);
    }

    public override void _Process(double delta)
    {
        if (Result != "RUNNING" || _checks is not { IsCompleted: true }) return;
        try
        {
            string? error = _checks.GetAwaiter().GetResult();
            Result = error == null ? "PASS" : "FAIL " + error;
        }
        catch (Exception error) { Result = "FAIL " + error.Message; }
        if (Result == "PASS") Console.WriteLine("PDA_LOCAL_MODEL=PASS transport/lifecycle only; inspect answers separately for semantic quality");
        else Console.Error.WriteLine("PDA_LOCAL_MODEL=" + Result);
    }

    public override void _ExitTree()
    {
        _lifetime.Cancel();
        _client?.Dispose();
        _lifetime.Dispose();
    }

    private async Task<string?> RunChecksAsync(string projectRoot, CancellationToken token)
    {
        var failures = new List<string>();
        void Check(bool condition, string message) { if (!condition) failures.Add(message); }
        try
        {
            _client = new LocalPdaQaClient(projectRoot);
            var warmTimer = Stopwatch.StartNew();
            await _client.WarmAsync(token).ConfigureAwait(false);
            double warmupMs = warmTimer.Elapsed.TotalMilliseconds;
            int? firstPid = _client.WorkerProcessId;
            Check(firstPid.HasValue, "no owned helper process after warmup");
            using (Process resident = Process.GetProcessById(firstPid!.Value))
            {
                double cpuBefore = resident.TotalProcessorTime.TotalMilliseconds;
                var idleTimer = Stopwatch.StartNew();
                await Task.Delay(250, token).ConfigureAwait(false);
                resident.Refresh();
                var measurement = new { type = "resident", pid = firstPid, warmupMs, workingSetMiB = resident.WorkingSet64 / 1048576.0,
                    idleObservationMs = idleTimer.Elapsed.TotalMilliseconds, idleCpuMs = resident.TotalProcessorTime.TotalMilliseconds - cpuBefore };
                _evidence.Add(measurement);
                Console.WriteLine("PDA_LOCAL_MODEL_RESIDENT " + JsonSerializer.Serialize(measurement));
            }
            await _client.WarmAsync(token).ConfigureAwait(false);
            Check(_client.WorkerProcessId == firstPid, "repeated warmup replaced the resident helper");
            PdaQaReply first = await Ask("active_light", "Why is it bright during the eclipse?",
                "Current readings: The gas giant is blocking direct starlight at the player's location. The artificial hub light is active and supplies local daylight. Known explanation: The ring's emitter network powers the artificial hub light during eclipses.", token).ConfigureAwait(false);
            Check(first.Error == null && first.Answer.Length > 0, "first local answer failed: " + first.Error);
            Check(firstPid.HasValue, "no owned helper process after first answer");
            PdaQaReply second = await Ask("unknown_vitals", "How much oxygen do I have left?",
                "Current readings: The player is outdoors. No oxygen, health, equipment or inventory readings are connected to this debug PDA.", token).ConfigureAwait(false);
            Check(second.Error == null && second.Answer.Length > 0, "warm answer failed: " + second.Error);
            Check(_client.WorkerProcessId == firstPid, "warm question did not reuse the loaded helper");
            PdaQaReply falsePremise = await Ask("false_premise_light", "Why has the ring created a false sun right now?",
                "Current readings: There is no eclipse at the player's location. The artificial hub light is inactive. The star is above the horizon and is the current daylight source.", token).ConfigureAwait(false);
            Check(falsePremise.Error == null && falsePremise.Answer.Length > 0, "false-premise sample transport failed: " + falsePremise.Error);
            PdaQaReply unknownRain = await Ask("rain_observation_unknown", "Is it raining?",
                "Current readings: Local precipitation potential is 0.8. The actual precipitation-effect provider is unavailable or uninitialized. Local rain observation is unknown. Known explanation: Precipitation potential describes conditions favorable for precipitation; it is not an observation or guarantee of rainfall.", token).ConfigureAwait(false);
            Check(unknownRain.Error == null && unknownRain.Answer.Length > 0, "unknown-rain sample transport failed: " + unknownRain.Error);
            Check(_client.WorkerProcessId == firstPid, "quality samples did not reuse the resident helper");

            await CancelAtStage("prefill", new string(' ', 1) + string.Concat(System.Linq.Enumerable.Repeat(
                "Recorded observation: The player is outdoors on the ring surface; this is a sensor sample with no additional conclusion. ", 50)), failures, token).ConfigureAwait(false);
            Check(_client.WorkerProcessId == firstPid, "prefill cancellation unloaded the resident model");
            await CancelAtStage("tokens", "Current readings: The player is outdoors. The gas giant is blocking direct starlight. The artificial hub light is active and supplies local daylight. Known explanation: The emitter network powers the hub light during eclipses.", failures, token).ConfigureAwait(false);
            Check(_client.WorkerProcessId == firstPid, "mid-generation cancellation unloaded the resident model");
            PdaQaReply afterCancel = await Ask("after_cancel", "Is the artificial hub light active?",
                "Current readings: The artificial hub light is inactive. No eclipse is occurring.", token).ConfigureAwait(false);
            Check(afterCancel.Error == null && afterCancel.Answer.Length > 0, "question after cancellation failed: " + afterCancel.Error);
            Check(_client.WorkerProcessId == firstPid, "post-cancel question replaced the resident helper");
            _client.Stop();
            Check(await IsStopped(firstPid, token).ConfigureAwait(false), "Stop left the resident helper alive");
            await _client.WarmAsync(token).ConfigureAwait(false);
            PdaQaReply restarted = await Ask("restart", "Is the artificial hub light active?",
                "Current readings: The artificial hub light is inactive. No eclipse is occurring.", token).ConfigureAwait(false);
            Check(restarted.Error == null && restarted.Answer.Length > 0, "restart failed: " + restarted.Error);
            int? restartPid = _client.WorkerProcessId;
            Check(restartPid.HasValue && restartPid != firstPid, "restart did not create a new owned helper");
            _client.Stop();
            Check(await IsStopped(restartPid, token).ConfigureAwait(false), "Stop left the helper alive");

            string missingRoot = Path.Combine(projectRoot, ".godot", "pda_debug", "local_model_test", "missing_model");
            string fixtureExe = Path.Combine(missingRoot, "prototypes", "pda-voice", "bin", "Release", "net10.0", "win-x64", "PdaVoicePrototype.exe");
            string fixtureRuntime = Path.Combine(missingRoot, "artifacts", "engine-upgrade", "dotnet", "dotnet.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(fixtureExe)!);
            Directory.CreateDirectory(Path.GetDirectoryName(fixtureRuntime)!);
            // These inert placeholders exercise preflight; model absence must be caught before either executable can start.
            File.WriteAllBytes(fixtureExe, Array.Empty<byte>());
            File.WriteAllBytes(fixtureRuntime, Array.Empty<byte>());
            try
            {
                using var missing = new LocalPdaQaClient(missingRoot);
                PdaQaReply missingReply = await missing.AskAsync("What is happening?", "No connected readings.", token).ConfigureAwait(false);
                Check(missingReply.Error?.Contains("GGUF model is missing", StringComparison.Ordinal) == true, "missing-model preflight did not return its actionable error");
                Check(missing.WorkerProcessId == null, "missing-model fixture launched a process");
            }
            finally { File.Delete(fixtureExe); File.Delete(fixtureRuntime); }
            Console.WriteLine("PDA_LOCAL_MODEL_QUALITY=REVIEW answers above are generated text, not verified explanations");
        }
        catch (Exception error) { failures.Add(error.GetType().Name + ": " + error.Message); }
        finally
        {
            _client?.Dispose();
            string evidencePath = Path.Combine(projectRoot, ".godot", "pda_debug", "resident_model_results.json");
            Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(new { verdict = failures.Count == 0 ? "PASS" : "FAIL",
                scope = "transport/lifecycle only", semanticQuality = "REVIEW generated answers against supplied context",
                model = "qwen3-0.6b", events = _evidence, failures }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failures.Count == 0 ? null : string.Join(" | ", failures);
    }

    private async Task CancelAtStage(string stage, string context, List<string> failures, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        long prior = _client!.LastRequestId;
        Task<PdaQaReply> pending = _client.AskAsync("Explain these current conditions in three sentences.", context, cancellation.Token);
        var timer = Stopwatch.StartNew();
        string marker = stage == "prefill" ? "PDA_QA_PREFILL:" : "PDA_QA_TOKENS:";
        while (!pending.IsCompleted && (timer.Elapsed.TotalSeconds < 10))
        {
            long id = _client.LastRequestId;
            string diagnostics = _client.DiagnosticTail;
            if (id > prior && diagnostics.Contains(marker + id + System.Environment.NewLine, StringComparison.Ordinal))
            {
                if (stage == "prefill" && diagnostics.Contains("PDA_QA_TOKENS:" + id + System.Environment.NewLine, StringComparison.Ordinal))
                    failures.Add("prefill probe reached token generation before cancellation");
                break;
            }
            await Task.Delay(5, token).ConfigureAwait(false);
        }
        if (pending.IsCompleted || timer.Elapsed.TotalSeconds >= 10) failures.Add(stage + " phase was not observed while inference was active");
        var cancelTimer = Stopwatch.StartNew();
        cancellation.Cancel();
        try { await pending.ConfigureAwait(false); failures.Add(stage + " cancellation returned an answer"); }
        catch (OperationCanceledException) { }
        var measurement = new { type = "cancellation", stage, cancellationMs = cancelTimer.Elapsed.TotalMilliseconds, pid = _client.WorkerProcessId };
        _evidence.Add(measurement);
        Console.WriteLine("PDA_LOCAL_MODEL_CANCEL " + JsonSerializer.Serialize(measurement));
    }

    private async Task<PdaQaReply> Ask(string name, string question, string context, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        PdaQaReply reply = await _client!.AskAsync(question, context, token).ConfigureAwait(false);
        var measurement = new { type = "sample", name, question, context, reply, wallMs = timer.Elapsed.TotalMilliseconds,
            pid = _client.WorkerProcessId, workingSetMiB = WorkingSetMiB(_client.WorkerProcessId) };
        _evidence.Add(measurement);
        Console.WriteLine("PDA_LOCAL_MODEL_SAMPLE " + JsonSerializer.Serialize(measurement));
        return reply;
    }

    private static double? WorkingSetMiB(int? pid)
    {
        if (!pid.HasValue) return null;
        try { using Process process = Process.GetProcessById(pid.Value); return process.WorkingSet64 / 1048576.0; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return null; }
    }

    private static async Task<bool> IsStopped(int? pid, CancellationToken token)
    {
        if (!pid.HasValue) return true;
        try
        {
            using Process process = Process.GetProcessById(pid.Value);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (ArgumentException) { return true; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }
}
