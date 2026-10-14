using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace PdaVoicePrototype;

public sealed record ModelSpec(string Key, string File, bool DisableThinking);

public sealed class LineResult
{
    public string EventId { get; set; } = "";
    public int Sample { get; set; }
    public string Authored { get; set; } = "";
    public string Raw { get; set; } = "";
    public string Final { get; set; } = "";
    public bool Rejected { get; set; }
    public string? RejectReason { get; set; }
    public int Tokens { get; set; }
    public double LatencyMs { get; set; }
    public double FirstTokenMs { get; set; }
    public double TokensPerSecond { get; set; }
}

public sealed class WorkerResult
{
    public string Model { get; set; } = "";
    public string File { get; set; } = "";
    public int Threads { get; set; }
    public bool Loaded { get; set; }
    public string? Error { get; set; }
    public double LoadSeconds { get; set; }
    public double WorkingSetAfterLoadMb { get; set; }
    public double PeakWorkingSetMb { get; set; }
    public double ModelFileMb { get; set; }
    public List<LineResult> Lines { get; set; } = new();
}

public static class LlmBench
{
    public static readonly ModelSpec[] Models =
    {
        new("qwen2-0.5b", "qwen2-0_5b-instruct-q4_k_m.gguf", false),
        new("qwen2.5-0.5b", "qwen2.5-0.5b-instruct-q4_k_m.gguf", false),
        new("qwen3-0.6b", "Qwen3-0.6B-Q8_0.gguf", true),
    };

    public const int Samples = 3;
    public const int MaxTokens = 48;
    public const float Temperature = 0.3f;

    public const string SystemPrompt =
        "You are the onboard computer of the player's survival suit, speaking over the suit radio. " +
        "Voice: calm, concise, dry, slightly clinical. Never emotional, never chatty, no jokes, no exclamation marks. " +
        "You rewrite a status announcement in your own voice. Output exactly ONE plain sentence of at most 25 words. " +
        "Merge the whole announcement into that single sentence (join clauses with a comma or semicolon). " +
        "Keep every number and every fact from the facts list exactly. Do not invent new facts. " +
        "Example. Authored line: \"Hull breach in sector 4. Seal the door.\" Facts: sector: 4. Reply: Hull breach detected in sector 4; sealing the door is advised.";

    // Single line, plain text, must end with punctuation. '.' inside a number (1.2) is allowed.
    public const string Grammar =
        "root ::= [A-Z] body [.!]\n" +
        "body ::= ([A-Za-z0-9 ,;'()%:/+-] | \".\" [0-9])*\n";

    public static string UserPrompt(PdaEvent ev) =>
        $"Authored line: \"{ev.Fallback}\"\nFacts: {ev.FactsText}\nRewrite the authored line as one sentence of at most 25 words, keeping every fact.";

    // ---------------- worker (one model, one thread count, own process so RAM numbers are clean) ----------------
    public static async Task<int> RunWorker(string modelKey, int threads, string outPath)
    {
        var spec = Models.First(m => m.Key == modelKey);
        var path = Path.Combine(Paths.Models, spec.File);
        var res = new WorkerResult { Model = spec.Key, File = spec.File, Threads = threads, ModelFileMb = new FileInfo(path).Length / 1048576.0 };
        var events = Events.Load();
        try
        {
            var sw = Stopwatch.StartNew();
            var mp = new ModelParams(path)
            {
                ContextSize = 1024,
                GpuLayerCount = 0,
                Threads = threads,
                BatchThreads = threads,
            };
            using var weights = LLamaWeights.LoadFromFile(mp);
            res.LoadSeconds = sw.Elapsed.TotalSeconds;
            res.WorkingSetAfterLoadMb = Process.GetCurrentProcess().WorkingSet64 / 1048576.0;
            res.Loaded = true;

            var executor = new StatelessExecutor(weights, mp);

            // Warm-up (first call pays one-time graph/cache costs); not recorded.
            await Generate(weights, executor, spec, events[0], seed: 1, record: null);

            foreach (var ev in events)
                for (int s = 0; s < Samples; s++)
                {
                    var line = new LineResult { EventId = ev.Id, Sample = s, Authored = ev.Fallback };
                    await Generate(weights, executor, spec, ev, seed: 1000 + s * 7919 + events.IndexOf(ev) * 31, record: line);
                    var reason = Validator.Check(ev, line.Raw);
                    line.Rejected = reason != null;
                    line.RejectReason = reason;
                    line.Final = reason == null ? line.Raw.Trim() : ev.Fallback;
                    res.Lines.Add(line);
                    Console.WriteLine($"  [{spec.Key} t{threads}] {ev.Id}#{s} {line.LatencyMs:F0}ms {(line.Rejected ? "REJECT " + reason : "ok")}: {line.Raw}");
                }
            res.PeakWorkingSetMb = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0;
        }
        catch (Exception ex)
        {
            res.Error = ex.GetType().Name + ": " + ex.Message;
            Console.WriteLine($"  [{spec.Key}] FAILED: {res.Error}");
        }
        File.WriteAllText(outPath, JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = true }));
        return res.Loaded ? 0 : 2;
    }

    static async Task Generate(LLamaWeights weights, StatelessExecutor executor, ModelSpec spec, PdaEvent ev, int seed, LineResult? record)
    {
        // Model's own chat template (ChatML for all three Qwen families) applied via llama.cpp.
        var template = new LLamaTemplate(weights) { AddAssistant = true };
        template.Add("system", SystemPrompt);
        template.Add("user", UserPrompt(ev));
        var prompt = Encoding.UTF8.GetString(template.Apply());
        // Qwen3 is a hybrid-thinking model; its template with enable_thinking=false pre-fills an empty think block.
        if (spec.DisableThinking) prompt += "<think>\n\n</think>\n\n";

        var inference = new InferenceParams
        {
            MaxTokens = MaxTokens,
            AntiPrompts = new List<string> { "<|im_end|>" },
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = Temperature,
                TopK = 20,
                TopP = 0.9f,
                Seed = (uint)seed,
                Grammar = new LLama.Sampling.Grammar(Grammar, "root"),
            },
        };

        var sb = new StringBuilder();
        int tokens = 0;
        double first = 0;
        var sw = Stopwatch.StartNew();
        await foreach (var piece in executor.InferAsync(prompt, inference))
        {
            if (tokens == 0) first = sw.Elapsed.TotalMilliseconds;
            tokens++;
            sb.Append(piece);
        }
        var total = sw.Elapsed.TotalMilliseconds;
        if (record == null) return;
        record.Raw = sb.ToString().Replace("<|im_end|>", "").Trim();
        record.Tokens = tokens;
        record.LatencyMs = total;
        record.FirstTokenMs = first;
        record.TokensPerSecond = tokens > 1 ? (tokens - 1) / ((total - first) / 1000.0) : 0;
    }

    // ---------------- parent orchestration ----------------
    public static async Task<int> RunAll(string[] args)
    {
        var wanted = Cli.Value(args, "--models")?.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var threadCounts = Cli.Value(args, "--threads")?.Split(',').Select(int.Parse).ToArray() ?? new[] { 8, 4 };
        var specs = Models.Where(m => wanted == null || wanted.Contains(m.Key)).ToList();
        Directory.CreateDirectory(Paths.Output);
        var results = new List<WorkerResult>();
        foreach (var spec in specs)
            foreach (var t in threadCounts)
            {
                var tmp = Path.Combine(Paths.Output, $"_worker_{spec.Key}_t{t}.json");
                if (File.Exists(tmp)) File.Delete(tmp);
                Console.WriteLine($"== {spec.Key} @ {t} threads");
                var psi = new ProcessStartInfo(Environment.ProcessPath!, $"llm-worker --model {spec.Key} --threads {t} --out \"{tmp}\"")
                    { UseShellExecute = false, RedirectStandardError = true }; // llama.cpp logs to stderr; drop it
                using var p = Process.Start(psi)!;
                p.ErrorDataReceived += (_, _) => { };
                p.BeginErrorReadLine();
                await p.WaitForExitAsync();
                if (File.Exists(tmp))
                {
                    results.Add(JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(tmp))!);
                    File.Delete(tmp);
                }
                else results.Add(new WorkerResult { Model = spec.Key, File = spec.File, Threads = t, Error = $"worker exited {p.ExitCode} without result" });
            }
        LlmReport.Write(results, Events.Load());
        return 0;
    }
}

public static class Cli
{
    public static string? Value(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
        return null;
    }
}
