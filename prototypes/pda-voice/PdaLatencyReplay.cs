using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using RingworldTechDemo.Pda;

namespace PdaVoicePrototype;

/// <summary>
/// [latency instrumentation] Replays contexts captured from a live game (tools/PdaLoadProbe.cs) through the real
/// LocalPdaQaClient + resident helper, recording per-request wall/TTFT/native prefill+decode and nvidia-smi samples.
/// Measurement only: no verdict, no quality grading. Output stays under .godot/pda_latency/.
/// </summary>
public static class PdaLatencyReplay
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<int> Run(string[] args)
    {
        string key = Cli.Value(args, "--model") ?? throw new ArgumentException("--model is required.");
        PdaMemoryMode mode = (Cli.Value(args, "--memory") ?? "vram") == "ram" ? PdaMemoryMode.SystemRam : PdaMemoryMode.GpuVram;
        int layers = int.Parse(Cli.Value(args, "--gpu-layers") ?? "99", CultureInfo.InvariantCulture);
        int threads = int.Parse(Cli.Value(args, "--threads") ?? "4", CultureInfo.InvariantCulture);
        string input = Cli.Value(args, "--input") ?? throw new ArgumentException("--input contexts.json is required.");
        string? imagePath = Cli.Value(args, "--image");
        string cell = Cli.Value(args, "--cell") ?? "cell";
        string load = Cli.Value(args, "--load") ?? "unknown";
        int repeats = int.Parse(Cli.Value(args, "--repeats") ?? "1", CultureInfo.InvariantCulture);
        string root = Directory.GetParent(Directory.GetParent(Paths.Root)!.FullName)!.FullName;
        string outDir = Path.Combine(root, ".godot", "pda_latency", "replay");
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, cell + "-" + load + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture) + ".json");

        using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(input));
        var items = doc.RootElement.GetProperty("items").EnumerateArray()
            .Where(i => !i.GetProperty(imagePath != null && i.TryGetProperty("policyImage", out _) ? "policyImage" : "policy").GetBoolean())
            .Select(i => (Id: i.GetProperty("id").GetString()!, Question: i.GetProperty("question").GetString()!,
                Context: i.GetProperty(imagePath != null ? "imageContext" : "context").GetString()!,
                Version: i.GetProperty("snapshotVersion").GetInt64()))
            .ToArray();
        if (items.Length == 0) throw new InvalidDataException("No model-reaching items in the input.");
        byte[]? png = imagePath != null ? await File.ReadAllBytesAsync(imagePath) : null;

        Environment.SetEnvironmentVariable("PDA_LATENCY_PROBE", "1"); // inherited by the helper: one-off context-creation timing
        var timer = Stopwatch.StartNew();
        using var client = new LocalPdaQaClient(root, key, threads, mode, mode == PdaMemoryMode.GpuVram ? layers : 99, useComparisonHelper: true, preloadVision: imagePath != null);
        var sampler = new PdaModelComparison.ComparisonSampler(client, timer);
        await sampler.CaptureBaselineAsync();
        sampler.Start();
        var rows = new List<Dictionary<string, object?>>();
        double helperStart = double.NaN;
        string ctxCreate = "";
        string? error = null;
        try
        {
            var start = Stopwatch.StartNew();
            using (var startup = new CancellationTokenSource(TimeSpan.FromSeconds(240))) await client.WarmAsync(startup.Token);
            helperStart = start.Elapsed.TotalMilliseconds;
            sampler.TrackCurrent();
            ctxCreate = System.Text.RegularExpressions.Regex.Match(client.DiagnosticTail, @"PDA_LATENCY_CTX_CREATE_MS=([0-9.,]+)").Groups[1].Value;
            Console.WriteLine($"REPLAY {cell}/{load}: context create {ctxCreate} ms; helper ready in {helperStart:0} ms; backend {client.BackendInfo}");
            // Request order: cold first request, every model item once (warm), then the first item again (warm).
            var order = new List<(int Index, string Phase)> { (0, "cold") };
            for (int r = 0; r < repeats; r++) for (int i = 0; i < items.Length; i++) order.Add((i, "warm"));
            foreach ((int index, string phase) in order)
            {
                var item = items[index];
                PdaImageInput? image = png != null ? new PdaImageInput(png, DateTimeOffset.UtcNow, item.Version) : null;
                double t0 = timer.Elapsed.TotalMilliseconds;
                long unix0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(200));
                PdaQaReply reply = await client.AskAsync(item.Question, item.Context, deadline.Token, image, _ => { });
                double t1 = timer.Elapsed.TotalMilliseconds;
                long unix1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                sampler.TrackCurrent();
                var gpu = sampler.Samples.Where(s => s.WallMilliseconds >= t0 && s.WallMilliseconds <= t1 && s.NvidiaDeviceWideCsv != null)
                    .Select(s => s.NvidiaDeviceWideCsv!.Split(',')).Where(c => c.Length >= 5).ToArray();
                double? util = gpu.Length == 0 ? null : gpu.Average(c => double.Parse(c[^1].Trim(), CultureInfo.InvariantCulture));
                double? vram = gpu.Length == 0 ? null : gpu.Max(c => double.Parse(c[^3].Trim(), CultureInfo.InvariantCulture));
                double tps = reply.DecodeMilliseconds > 0 && reply.DecodeEvalTokens > 0 ? reply.DecodeEvalTokens / (reply.DecodeMilliseconds / 1000d) : 0;
                double pps = 0; // prefill is not separately measured; see firstTokenMs and contextCreateMs
                rows.Add(new()
                {
                    ["id"] = item.Id, ["phase"] = phase, ["question"] = item.Question, ["contextChars"] = item.Context.Length,
                    ["unixStartMs"] = unix0, ["unixEndMs"] = unix1, ["wallMs"] = t1 - t0,
                    ["helperElapsedMs"] = reply.ElapsedMilliseconds, ["firstTokenMs"] = reply.FirstTokenMilliseconds, ["firstPartialMs"] = reply.FirstPartialMilliseconds,
                    ["prefillMs"] = reply.PrefillMilliseconds, ["decodeMs"] = reply.DecodeMilliseconds,
                    ["promptTokens"] = reply.PromptTokens, ["promptEvalTokens"] = reply.PromptEvalTokens,
                    ["decodeEvalTokens"] = reply.DecodeEvalTokens, ["outputTokens"] = reply.OutputTokens, ["outputPieces"] = reply.OutputPieces,
                    ["prefillTokPerS"] = pps, ["decodeTokPerS"] = tps, ["imageUsed"] = reply.ImageUsed,
                    ["imageSetupMs"] = reply.ImageSetupMilliseconds, ["imagePromptTokens"] = reply.ImagePromptTokens,
                    ["projector"] = reply.ProjectorDevice, ["gpuUtilMean"] = util, ["vramMaxMiB"] = vram, ["gpuSamples"] = gpu.Length,
                    ["answer"] = reply.Answer, ["rawAnswer"] = reply.RawAnswer, ["error"] = reply.Error, ["renderedPrompt"] = reply.RenderedPrompt
                });
                Console.WriteLine($"  {phase} {item.Id}: wall {t1 - t0:0} ms, ttft {reply.FirstTokenMilliseconds:0}, prompt {reply.PromptEvalTokens} tok, decode {reply.DecodeMilliseconds:0} ms/{reply.DecodeEvalTokens} tok ({tps:0.0} t/s), out {reply.OutputTokens}, gpu {util:0}% vram {vram:0}{(reply.Error != null ? " ERROR " + reply.Error : "")}");
            }
        }
        catch (Exception e) { error = e.GetType().Name + ": " + e.Message; helperStart = double.NaN; Console.Error.WriteLine(error); }
        await sampler.StopAsync();
        var result = new Dictionary<string, object?>
        {
            ["cell"] = cell, ["load"] = load, ["model"] = key, ["memory"] = mode.ToString(), ["requestedLayers"] = layers, ["threads"] = threads,
            ["image"] = imagePath, ["helperReadyMs"] = double.IsFinite(helperStart) ? helperStart : null, ["contextCreateMs"] = ctxCreate, ["backend"] = client.BackendInfo, ["error"] = error, ["rows"] = rows,
            ["samples"] = sampler.Samples.Select(s => new { s.WallMilliseconds, s.WorkingSetBytes, s.PrivateBytes, s.NvidiaDeviceWideCsv, s.Phase })
        };
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(result, Json));
        sampler.Dispose();
        Console.WriteLine("REPLAY_RESULT=" + outPath);
        return error == null ? 0 : 1;
    }
}
