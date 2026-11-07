using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RingworldTechDemo.Pda;

namespace PdaVoicePrototype;

/// <summary>Standalone, one-resident-helper comparison. Scores transport/backend execution only; quality requires human review.</summary>
public static class PdaModelComparison
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string[] TrialKeys = { "qwen3-8b-q4", "granite-3.3-8b-q4", "llama-3.1-8b-q4", "gemma-3-4b-q4" };
    private const int TotalBudgetSeconds = 825, RequestDeadlineSeconds = 75, StartupDeadlineCapSeconds = 240;

    public static async Task<int> Run(string[] args)
    {
        string key = Cli.Value(args, "--model") ?? throw new ArgumentException("--model is required.");
        if (!TrialKeys.Contains(key)) throw new ArgumentException("Select one approved comparison model key.");
        string modeText = Cli.Value(args, "--memory") ?? "ram";
        PdaMemoryMode mode = modeText switch { "ram" => PdaMemoryMode.SystemRam, "vram" => PdaMemoryMode.GpuVram, _ => throw new ArgumentException("--memory must be ram or vram.") };
        int threads = ParseInt(args, "--threads", 4, 1, 16), gpuLayers = ParseInt(args, "--gpu-layers", 8, 1, 99);
        string contextMode = Cli.Value(args, "--context") ?? "baseline";
        if (contextMode is not ("baseline" or "focused")) throw new ArgumentException("--context must be baseline or focused.");
        string guardrailsText = Cli.Value(args, "--guardrails") ?? "false";
        if (!bool.TryParse(guardrailsText, out bool guardrails)) throw new ArgumentException("--guardrails must be true or false.");
        string configurationLabel = mode == PdaMemoryMode.GpuVram ? "vram-gpu" + gpuLayers : "ram";
        string label = Cli.Value(args, "--label") ?? "pda_8b";
        if (!Regex.IsMatch(label, "^[A-Za-z0-9_-]{1,64}$")) throw new ArgumentException("Use a short filename-safe label.");
        string root = Directory.GetParent(Directory.GetParent(Paths.Root)!.FullName)!.FullName;
        string directory = Path.Combine(root, ".godot", "pda_8b", "comparison", label,
            key + "-" + configurationLabel + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        var result = new ComparisonResult
        {
            ModelKey = key, MemoryMode = modeText, ConfigurationLabel = configurationLabel, Threads = threads, RequestedGpuLayers = mode == PdaMemoryMode.GpuVram ? gpuLayers : 0,
            StartedAtUtc = DateTimeOffset.UtcNow, ResultDirectory = directory,
            ContextMode = contextMode, Guardrails = guardrails,
            Parameters = RunParameters(threads, mode == PdaMemoryMode.GpuVram ? gpuLayers : 0, null)
        };
        LocalPdaQaClient? client = null;
        ComparisonSampler? sampler = null;
        var timer = Stopwatch.StartNew();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(TotalBudgetSeconds));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; lifetime.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            PdaModelSpec spec = PdaModelCatalog.Get(key);
            if (!spec.ComparisonOnly && key is not ("qwen3-8b-q4" or "gemma-3-4b-q4")) throw new InvalidOperationException("This harness accepts approved comparison model entries.");
            result.ModelCatalogEntry = spec;
            result.Parameters = RunParameters(threads, result.RequestedGpuLayers, Math.Min(spec.StartupTimeoutSeconds, StartupDeadlineCapSeconds));
            string candidatePath = Path.Combine(root, "resources", "pda", "model_artifacts.json");
            using JsonDocument candidates = JsonDocument.Parse(await File.ReadAllTextAsync(candidatePath, lifetime.Token));
            JsonElement metadata = candidates.RootElement.EnumerateArray().Single(c => c.GetProperty("Key").GetString() == key);
            result.CandidateMetadata = metadata.Clone();
            string relative = metadata.GetProperty("RelativePath").GetString() ?? throw new InvalidDataException("Candidate RelativePath missing.");
            string modelPath = Path.GetFullPath(Path.Combine(Paths.Models, relative));
            if (!modelPath.StartsWith(Path.GetFullPath(Paths.Models) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Candidate path escapes model directory.");
            if (!string.Equals(modelPath, Path.GetFullPath(Path.Combine(Paths.Models, spec.RelativeFile)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Catalog and pinned metadata refer to different model files.");
            if (!File.Exists(modelPath)) throw new FileNotFoundException("Comparison GGUF is not installed; this harness never downloads models.", modelPath);
            result.ModelPath = modelPath;
            result.ModelBytes = new FileInfo(modelPath).Length;
            await using (FileStream stream = File.OpenRead(modelPath)) result.ModelSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, lifetime.Token)).ToLowerInvariant();
            if (!string.Equals(result.ModelSha256, metadata.GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase)
                || result.ModelBytes != metadata.GetProperty("Bytes").GetInt64()) throw new InvalidDataException("Installed candidate does not match pinned SHA-256/size metadata.");
            string casesPath = Path.Combine(root, "resources", "pda", "model_comparison_cases.json");
            string knowledgePath = Path.Combine(root, "resources", "pda", "debug_knowledge.json");
            string casesText = await File.ReadAllTextAsync(casesPath, lifetime.Token);
            string knowledgeText = await File.ReadAllTextAsync(knowledgePath, lifetime.Token);
            result.CasesSha256 = Hash(casesText); result.KnowledgeSha256 = Hash(knowledgeText);
            using JsonDocument cases = JsonDocument.Parse(casesText);
            if (cases.RootElement.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Unknown comparison fixture schema.");
            JsonElement[] scored = cases.RootElement.GetProperty("cases").EnumerateArray().ToArray();
            if (scored.Length != 10 || scored.Select(c => c.GetProperty("id").GetString()).Distinct().Count() != 10) throw new InvalidDataException("Comparison requires exactly ten distinct scored cases.");
            Dictionary<string, PdaStateEvidence>? evidence = null;
            if (contextMode == "focused" || guardrails)
            {
                string evidenceText = await File.ReadAllTextAsync(Path.Combine(root, "resources", "pda", "model_comparison_evidence.json"), lifetime.Token);
                result.EvidenceSha256 = Hash(evidenceText);
                using JsonDocument addon = JsonDocument.Parse(evidenceText);
                if (addon.RootElement.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Unknown typed evidence fixture schema.");
                evidence = JsonSerializer.Deserialize<Dictionary<string, PdaStateEvidence>>(addon.RootElement.GetProperty("cases").GetRawText(),
                    new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })
                    ?? throw new InvalidDataException("Typed evidence fixture is empty.");
                string[] requiredIds = scored.Select(c => c.GetProperty("id").GetString()!).Append(cases.RootElement.GetProperty("warmup").GetProperty("id").GetString()!).ToArray();
                if (evidence.Count != requiredIds.Length || requiredIds.Any(id => !evidence.ContainsKey(id))) throw new InvalidDataException("Typed evidence IDs must match all ten cases and warmup exactly.");
            }
            IReadOnlyList<PdaKnowledgeEntry> knowledge = PdaContextComposer.ParseKnowledge(knowledgeText);
            client = new LocalPdaQaClient(root, key, threads, mode, gpuLayers, useComparisonHelper: true);
            sampler = new ComparisonSampler(client, timer);
            await sampler.CaptureBaselineAsync();
            sampler.Start();
            var load = Stopwatch.StartNew();
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                startup.CancelAfter(TimeSpan.FromSeconds(Math.Min(spec.StartupTimeoutSeconds, StartupDeadlineCapSeconds)));
                await client.WarmAsync(startup.Token);
            }
            result.ColdHelperStartMilliseconds = load.Elapsed.TotalMilliseconds;
            sampler.TrackCurrent();
            result.Backend = client.BackendInfo;
            result.BackendVerified = VerifyBackend(result.Backend, mode, gpuLayers);
            if (!result.BackendVerified) throw new InvalidOperationException("Actual backend/offloaded-layer handshake does not match requested controlled mode.");
            Save(result, sampler, directory);

            result.Warmup = await AskCase(client, cases.RootElement, cases.RootElement.GetProperty("warmup"), knowledge, true, spec.RequestTimeoutSeconds, lifetime.Token, contextMode, guardrails, evidence);
            sampler.TrackCurrent();
            Save(result, sampler, directory);
            if (result.Warmup.Error != null) throw new InvalidOperationException("Unscored warmup failed: " + result.Warmup.Error);
            foreach (JsonElement item in scored)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                ComparisonCase row = await AskCase(client, cases.RootElement, item, knowledge, false, spec.RequestTimeoutSeconds, lifetime.Token, contextMode, guardrails, evidence);
                result.Cases.Add(row);
                sampler.TrackCurrent();
                Save(result, sampler, directory);
                Console.WriteLine($"{key}/{configurationLabel} {row.Id}: {row.WallMilliseconds:0} ms wall, {row.Reply?.FirstTokenMilliseconds:0} ms TTFT, {row.Reply?.PromptTokens} prompt tokens; {(row.Error == null ? "transport complete; quality ungraded" : row.Error)}");
            }
            if (contextMode == "focused")
            {
                var authored = new List<PdaConversationTurn>
                {
                    new("nickname", DateTimeOffset.FromUnixTimeSeconds(1000), "Call the artificial hub light Lantern.",
                        "I'll refer to the artificial hub light as Lantern in our conversation.", "synthetic-fixture", 1, DateTimeOffset.FromUnixTimeSeconds(999), PdaAnswerKind.Model)
                };
                for (int i = 0; i < 12; i++)
                    authored.Add(new PdaConversationTurn("unrelated-" + i, DateTimeOffset.FromUnixTimeSeconds(1001 + i),
                        "Please acknowledge note " + i + ".", "Acknowledged note " + i + ".", "synthetic-fixture", 1,
                        DateTimeOffset.FromUnixTimeSeconds(999), PdaAnswerKind.Model));
                var staleRain = new[]
                {
                    new PdaConversationTurn("stale-rain", DateTimeOffset.FromUnixTimeSeconds(900), "Is it raining here?",
                        "The local effects are in rain mode with drive 0.7.", "synthetic-fixture", 0, DateTimeOffset.FromUnixTimeSeconds(899), PdaAnswerKind.Model)
                };
                var probeEvidence = new Dictionary<string, PdaStateEvidence>
                {
                    ["memory_nickname_recall"] = evidence!["warmup_unscored"],
                    ["memory_stale_rain_current_zero"] = evidence["warmup_unscored"]
                };
                var probes = new[]
                {
                    (Id: "memory_nickname_recall", Question: "What nickname did I give the artificial hub light?", Conversation: authored.ToArray(),
                        Expected: "The authored older user turn named the artificial hub light Lantern.", Nickname: (string?)"Lantern"),
                    (Id: "memory_stale_rain_current_zero", Question: "Is it raining here?", Conversation: staleRain,
                        Expected: "The current initialized local precipitation-effect drive is zero. Stale assistant dialogue must not replace current typed evidence.", Nickname: (string?)null)
                };
                foreach (var probe in probes)
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    using JsonDocument item = JsonDocument.Parse(JsonSerializer.Serialize(new
                    {
                        id = probe.Id, question = probe.Question, overrides = Array.Empty<object>(),
                        expected_facts = new[] { probe.Expected }, rubric = "Unscored synthetic conversation integration probe; semantic correctness requires human review."
                    }));
                    ComparisonCase row = await AskCase(client, cases.RootElement, item.RootElement, knowledge, false,
                        Math.Min(spec.RequestTimeoutSeconds, 25), lifetime.Token, contextMode, guardrails, probeEvidence,
                        probe.Conversation, true, probe.Nickname);
                    result.MemoryProbes.Add(row);
                    sampler.TrackCurrent();
                    Save(result, sampler, directory);
                }
            }
        }
        catch (Exception error)
        {
            result.Error = error.GetType().Name + ": " + error.Message;
            Console.Error.WriteLine(result.Error);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (sampler != null) await sampler.StopAsync();
            result.Backend ??= client?.BackendInfo;
            result.DiagnosticTail = client?.DiagnosticTail;
            client?.Dispose();
            result.OwnedHelperExitConfirmed = sampler == null || await sampler.ConfirmExitAsync();
            if (sampler != null)
            {
                result.HelperProcesses = sampler.Processes;
                result.Samples = sampler.Samples;
                sampler.Dispose();
            }
            result.HelperReused = result.HelperProcesses.Count == 1 && result.Cases.All(c => c.WorkerProcessId == result.HelperProcesses[0].ProcessId)
                && result.Warmup?.WorkerProcessId == result.HelperProcesses.FirstOrDefault()?.ProcessId;
            result.TransportVerdict = result.Error == null && result.Cases.Count == 10 && result.Cases.All(c => c.Error == null) && result.OwnedHelperExitConfirmed && result.HelperReused ? "pass" : "fail";
            result.BackendVerdict = result.BackendVerified ? "pass" : "fail";
            result.MemoryProbeExecutionVerdict = contextMode != "focused" ? "not_requested"
                : result.MemoryProbes.Count == 2 && result.MemoryProbes.All(p => p.Error == null) ? "pass_ungraded" : "fail";
            result.FinishedAtUtc = DateTimeOffset.UtcNow;
            result.TotalWallMilliseconds = timer.Elapsed.TotalMilliseconds;
            result.ObservedPeakWorkingSetBytes = result.Samples.Max(s => s.WorkingSetBytes) ?? 0;
            result.ObservedPeakPrivateBytes = result.Samples.Max(s => s.PrivateBytes) ?? 0;
            result.OsPeakWorkingSetBytes = result.Samples.Max(s => s.OsPeakWorkingSetBytes) ?? 0;
            foreach (MemorySample sample in result.Samples)
                foreach (string line in (sample.NvidiaDeviceWideCsv ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] columns = line.Split(',');
                    if (columns.Length < 5 || !long.TryParse(columns[^3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long used)) continue;
                    string index = columns[0].Trim();
                    if (sample.Phase == "before helper startup") result.DeviceWideBaselineMiBByIndex[index] = used;
                    if (!result.DeviceWideObservedPeakMiBByIndex.TryGetValue(index, out long previous) || used > previous) result.DeviceWideObservedPeakMiBByIndex[index] = used;
                }
            Save(result, null, directory);
        }
        string marker = contextMode == "focused" ? "PDA_FOCUSED_COMPARISON" : mode == PdaMemoryMode.GpuVram ? "PDA_MODEL_COMPARISON_VRAM" : "PDA_MODEL_COMPARISON_RAM";
        bool success = result.TransportVerdict == "pass" && result.BackendVerdict == "pass" && result.MemoryProbeExecutionVerdict != "fail";
        Console.WriteLine(marker + "=" + (success ? "PASS" : "FAIL") + (success ? "" : " see partial results"));
        Console.WriteLine("Human quality verdict: REQUIRED; results: " + Path.Combine(directory, "results.json"));
        return success ? 0 : 1;
    }

    private static async Task<ComparisonCase> AskCase(LocalPdaQaClient client, JsonElement fixture, JsonElement item,
        IReadOnlyList<PdaKnowledgeEntry> knowledge, bool warmup, double requestSeconds, CancellationToken lifetime,
        string contextMode, bool guardrails, IReadOnlyDictionary<string, PdaStateEvidence>? evidence,
        IReadOnlyList<PdaConversationTurn>? conversation = null, bool memoryProbe = false, string? expectedNickname = null)
    {
        var row = new ComparisonCase { Id = item.GetProperty("id").GetString()!, Question = item.GetProperty("question").GetString()!, UnscoredWarmup = warmup,
            UnscoredMemoryProbe = memoryProbe, ConversationInputs = conversation, ExpectedNicknameForHumanReview = expectedNickname,
            ExpectedFacts = item.GetProperty("expected_facts").EnumerateArray().Select(v => v.GetString()!).ToArray(), Rubric = item.GetProperty("rubric").GetString()! };
        var watch = Stopwatch.StartNew();
        try
        {
            PdaContextFact[] original = fixture.GetProperty("base_facts").EnumerateArray().Select(Fact).ToArray();
            var facts = original.ToList();
            foreach (JsonElement replacement in item.GetProperty("overrides").EnumerateArray())
            {
                PdaContextFact fact = Fact(replacement);
                int index = facts.FindIndex(f => f.Id == fact.Id);
                if (index >= 0) facts[index] = fact; else facts.Add(fact);
            }
            PdaRecentEvent[] history = item.TryGetProperty("recent_events", out JsonElement recent)
                ? recent.EnumerateArray().Select(e => new PdaRecentEvent(e.GetProperty("text").GetString()!, e.GetProperty("observed_at_seconds").GetDouble(), e.GetProperty("source").GetString()!)).ToArray()
                : Array.Empty<PdaRecentEvent>();
            var snapshot = new PdaContextSnapshot(fixture.GetProperty("snapshot_version").GetInt64(),
                DateTimeOffset.Parse(fixture.GetProperty("captured_at_utc").GetString()!, CultureInfo.InvariantCulture), facts.AsReadOnly(), knowledge, Array.AsReadOnly(history), evidence?[row.Id]);
            row.StateEvidence = snapshot.StateEvidence;
            if (memoryProbe) row.ContextWithoutConversation = PdaContextComposer.ComposeFocused(snapshot, row.Question).ContextText;
            PdaQuestionContext prepared = contextMode == "focused" ? PdaContextComposer.ComposeFocused(snapshot, row.Question, conversation) : PdaContextComposer.Compose(snapshot, row.Question);
            row.Context = prepared.ContextText; row.ContextInspection = prepared.DebugText;
            row.ContextSha256 = Hash(row.Context); row.ContextCharacters = row.Context.Length;
            if (prepared.DebugText.Contains("OMITTED (context budget)", StringComparison.Ordinal)) throw new InvalidDataException("A comparison fixture exceeded the complete-entry context budget.");
            row.WorkerProcessId = client.WorkerProcessId;
            using var question = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            row.RequestDeadlineSeconds = Math.Min(requestSeconds, RequestDeadlineSeconds);
            question.CancelAfter(TimeSpan.FromSeconds(row.RequestDeadlineSeconds));
            if (guardrails && PdaAnswerPolicy.TryAnswer(row.Question, snapshot, out string policyAnswer, out string policyReason))
            {
                row.AnswerKind = PdaAnswerKind.Policy;
                row.PolicyReason = policyReason;
                row.Reply = new PdaQaReply(policyAnswer, "policy", 0);
            }
            else
            {
                row.ModelCalled = true;
                row.Reply = await client.AskAsync(row.Question, row.Context, question.Token);
                if (guardrails && row.Reply.Error == null)
                {
                    string guarded = PdaAnswerPolicy.GuardGeneratedAnswer(row.Reply.Answer, out bool replaced);
                    if (replaced)
                    {
                        row.GeneratedAnswerReplaced = true;
                        row.AnswerKind = PdaAnswerKind.Policy;
                        row.PolicyReason = "generated_action_claim_replaced";
                        row.Reply = row.Reply with { Answer = guarded, RawAnswer = row.Reply.RawAnswer ?? row.Reply.Answer };
                    }
                }
            }
            row.WorkerProcessId ??= client.WorkerProcessId;
            row.Error = row.Reply.Error;
            row.ExecutionOutcome = row.Error == null ? "answer_available_for_human_review"
                : row.Error.Contains("timed out", StringComparison.OrdinalIgnoreCase) ? "performance_runtime_timeout_unevaluated" : "execution_or_cleaning_error_ungraded";
            row.PromptTokenRange = !row.ModelCalled ? "policy response; no model tokens or inference speed" : row.Reply.PromptTokens is >= 500 and <= 1000 ? "target range 500-1000" : "outside target range; retain for review, not a quality verdict";
        }
        catch (OperationCanceledException error)
        {
            row.Error = error.GetType().Name + ": " + error.Message;
            row.ExecutionOutcome = lifetime.IsCancellationRequested ? "invocation_cancelled_or_budget_exhausted_unevaluated" : "performance_request_deadline_exceeded_unevaluated";
        }
        catch (Exception error) { row.Error = error.GetType().Name + ": " + error.Message; row.ExecutionOutcome = "execution_error_unevaluated"; }
        row.WallMilliseconds = watch.Elapsed.TotalMilliseconds;
        return row;
    }

    private static PdaContextFact Fact(JsonElement value) => new(value.GetProperty("id").GetString()!, value.GetProperty("topic").GetString()!,
        value.GetProperty("text").GetString()!, value.GetProperty("source").GetString()!, value.TryGetProperty("model_text", out JsonElement compact) ? compact.GetString() : null);
    private static bool VerifyBackend(PdaBackendInfo? info, PdaMemoryMode mode, int layers) => info != null && info.TotalLayers > 0 && (mode == PdaMemoryMode.SystemRam
        ? info.Backend.Equals("cpu", StringComparison.OrdinalIgnoreCase) && info.OffloadedLayers == 0
        : info.Backend.Equals("cuda", StringComparison.OrdinalIgnoreCase) && info.OffloadedLayers == Math.Min(layers, info.TotalLayers) && !string.IsNullOrWhiteSpace(info.Device));
    private static int ParseInt(string[] args, string key, int fallback, int min, int max)
    {
        int value = int.Parse(Cli.Value(args, key) ?? fallback.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return value >= min && value <= max ? value : throw new ArgumentOutOfRangeException(key);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static object RunParameters(int threads, int gpuLayers, int? startupDeadlineSeconds) => new
    {
        contextTokens = 4096, maxOutputTokens = 160, seed = 42, temperature = 0.2, topK = 20, topP = 0.9,
        cpuThreads = threads, requestedGpuLayers = gpuLayers, benchmarkRequestDeadlineSeconds = RequestDeadlineSeconds,
        totalBudgetSeconds = TotalBudgetSeconds, startupDeadlineSeconds
    };

    private static void Save(ComparisonResult result, ComparisonSampler? sampler, string directory)
    {
        if (sampler != null) { result.Samples = sampler.Samples; result.HelperProcesses = sampler.Processes; }
        string path = Path.Combine(directory, "results.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(result, Json));
        File.Move(path + ".tmp", path, true);
        var md = new StringBuilder($"# PDA comparison: {result.ModelKey} / {result.ConfigurationLabel}\n\nSynthetic snapshots; one resident local helper. Human quality review is required. A transport PASS is not semantic correctness.\n\nTransport: {result.TransportVerdict}; backend: {result.BackendVerdict}; owned helper exited: {result.OwnedHelperExitConfirmed}.\n\nGPU layers requested: {result.RequestedGpuLayers}; actual: {result.ActualGpuLayers?.ToString() ?? "unverified"}/{result.TotalModelLayers?.ToString() ?? "unknown"}; layer offload fraction: {result.LayerOffloadFraction?.ToString("P1", CultureInfo.InvariantCulture) ?? "unverified"}. Layer fraction does not measure the fraction of bytes in VRAM. Full GPU weights still require RAM and runtime buffers; this isolated run does not measure game FPS.\n\nNew helper readiness: {result.ColdHelperStartMilliseconds:0} ms (OS file cache uncontrolled; SHA verification runs first). Total wall: {result.TotalWallMilliseconds:0} ms.\n\n");
        if (result.Error != null) md.Append("Error: ").Append(result.Error).Append("\n\n");
        md.Append("Context mode: ").Append(result.ContextMode).Append("; guardrails: ").Append(result.Guardrails)
            .Append(". Scored model calls: ").Append(result.ModelCallCount).Append("; policy-only answers: ").Append(result.PolicyOnlyCount)
            .Append("; generated answers replaced: ").Append(result.GeneratedReplacementCount)
            .Append(". Model-call wall total: ").Append(result.ModelCallWallMilliseconds.ToString("0", CultureInfo.InvariantCulture))
            .Append(" ms; policy-only wall total: ").Append(result.PolicyOnlyWallMilliseconds.ToString("0", CultureInfo.InvariantCulture))
            .Append(" ms. Policy responses are excluded from model speed; unscored warmup is excluded from both totals.\n\n");
        md.Append("Unscored memory probe execution: ").Append(result.MemoryProbeExecutionVerdict)
            .Append(". Memory probe answers remain ungraded; they are excluded from the ten scored cases and all model/policy timing totals above.\n\n");
        IEnumerable<ComparisonCase> rows = (result.Warmup is { } warmup ? new[] { warmup }.Concat(result.Cases) : result.Cases).Concat(result.MemoryProbes);
        foreach (ComparisonCase row in rows)
        {
            md.Append("## ").Append(row.Id).Append(row.UnscoredWarmup ? " (unscored warmup)" : row.UnscoredMemoryProbe ? " (unscored memory probe)" : "").Append("\n\nQuestion: ").Append(row.Question)
                .Append("\n\nAnswer: ").Append(row.Reply?.Answer ?? "<no cleaned answer>").Append("\n\nError: ").Append(row.Error ?? "none")
                .Append("\n\nAnswer kind: ").Append(row.AnswerKind).Append("; model called: ").Append(row.ModelCalled).Append("; policy reason: ").Append(row.PolicyReason ?? "none")
                .Append("\n\nExecution outcome: ").Append(row.ExecutionOutcome).Append("; request deadline: ").Append(row.RequestDeadlineSeconds).Append(" seconds. Timeout means a performance limit and an unevaluated answer, not a semantic failure.")
                .Append("\n\nWall / first output / worker total: ").Append(row.WallMilliseconds.ToString("0", CultureInfo.InvariantCulture)).Append(" / ")
                .Append(row.Reply?.FirstTokenMilliseconds.ToString("0", CultureInfo.InvariantCulture) ?? "unknown").Append(" / ")
                .Append(row.Reply?.ElapsedMilliseconds.ToString("0", CultureInfo.InvariantCulture) ?? "unknown").Append(" ms. Prompt tokens: ").Append(row.Reply?.PromptTokens)
                .Append("; output pieces: ").Append(row.Reply?.OutputPieces).Append("; output tokens: ").Append(row.Reply?.OutputTokens?.ToString() ?? "unavailable")
                .Append(".\n\nExpected facts for HUMAN review:\n");
            foreach (string fact in row.ExpectedFacts) md.Append("\n- ").Append(fact);
            md.Append("\n\nRubric: ").Append(row.Rubric).Append("\n\nQuality: UNGRADED. Complete raw answers, rendered prompts, supplied context and provenance are in results.json.\n\n");
        }
        File.WriteAllText(Path.Combine(directory, "review.md"), md.ToString());
    }

    public sealed class ComparisonResult
    {
        public string ContextMode { get; set; } = "baseline";
        public bool Guardrails { get; set; }
        public string? EvidenceSha256 { get; set; }
        public int ModelCallCount => Cases.Count(c => c.ModelCalled);
        public string MemoryProbeExecutionVerdict { get; set; } = "not_requested";
        public List<ComparisonCase> MemoryProbes { get; } = new();
        public int PolicyOnlyCount => Cases.Count(c => !c.ModelCalled && c.AnswerKind == PdaAnswerKind.Policy);
        public int GeneratedReplacementCount => Cases.Count(c => c.GeneratedAnswerReplaced);
        public double ModelCallWallMilliseconds => Cases.Where(c => c.ModelCalled).Sum(c => c.WallMilliseconds);
        public double PolicyOnlyWallMilliseconds => Cases.Where(c => !c.ModelCalled && c.AnswerKind == PdaAnswerKind.Policy).Sum(c => c.WallMilliseconds);
        public string ModelKey { get; set; } = "";
        public string MemoryMode { get; set; } = "";
        public string ConfigurationLabel { get; set; } = "";
        public string ResultDirectory { get; set; } = "";
        public int Threads { get; set; }
        public int RequestedGpuLayers { get; set; }
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset FinishedAtUtc { get; set; }
        public object? Parameters { get; set; }
        public PdaModelSpec? ModelCatalogEntry { get; set; }
        public JsonElement? CandidateMetadata { get; set; }
        public string? ModelPath { get; set; }
        public string? ModelSha256 { get; set; }
        public string? CasesSha256 { get; set; }
        public string? KnowledgeSha256 { get; set; }
        public string? Error { get; set; }
        public string? DiagnosticTail { get; set; }
        public long ModelBytes { get; set; }
        public double ColdHelperStartMilliseconds { get; set; }
        public string ColdReadinessDefinition { get; } = "New helper process startup/model readiness, not cold OS/disk loading. SHA verification reads the model first; OS file cache is uncontrolled.";
        public double TotalWallMilliseconds { get; set; }
        public long ObservedPeakWorkingSetBytes { get; set; }
        public long ObservedPeakPrivateBytes { get; set; }
        public long OsPeakWorkingSetBytes { get; set; }
        public Dictionary<string, long> DeviceWideBaselineMiBByIndex { get; } = new();
        public Dictionary<string, long> DeviceWideObservedPeakMiBByIndex { get; } = new();
        public string NvidiaAttribution { get; } = "Device-wide sampled usage includes other processes and driver allocations; not exact helper attribution.";
        public PdaBackendInfo? Backend { get; set; }
        public int? ActualGpuLayers => Backend?.OffloadedLayers;
        public int? TotalModelLayers => Backend?.TotalLayers;
        public int? ExpectedGpuLayers => Backend is { TotalLayers: > 0 } ? Math.Min(RequestedGpuLayers, Backend.TotalLayers) : null;
        public double? LayerOffloadFraction => Backend is { TotalLayers: > 0 } ? (double)Backend.OffloadedLayers / Backend.TotalLayers : null;
        public string WeightPlacementDefinition { get; } = "Reported layer fraction is not a VRAM/RAM byte split. Full GPU weights still use host RAM and runtime buffers; standalone results do not establish game FPS.";
        public bool BackendVerified { get; set; }
        public bool OwnedHelperExitConfirmed { get; set; }
        public bool HelperReused { get; set; }
        public string TransportVerdict { get; set; } = "incomplete";
        public string BackendVerdict { get; set; } = "incomplete";
        public string HumanQualityVerdict { get; set; } = "requires_human_review";
        public ComparisonCase? Warmup { get; set; }
        public List<ComparisonCase> Cases { get; } = new();
        public IReadOnlyList<HelperProcess> HelperProcesses { get; set; } = Array.Empty<HelperProcess>();
        public IReadOnlyList<MemorySample> Samples { get; set; } = Array.Empty<MemorySample>();
    }
    public sealed class ComparisonCase
    {
        public PdaStateEvidence? StateEvidence { get; set; }
        public PdaAnswerKind AnswerKind { get; set; } = PdaAnswerKind.Model;
        public string? PolicyReason { get; set; }
        public bool ModelCalled { get; set; }
        public bool GeneratedAnswerReplaced { get; set; }
        public bool UnscoredMemoryProbe { get; set; }
        public IReadOnlyList<PdaConversationTurn>? ConversationInputs { get; set; }
        public string? ContextWithoutConversation { get; set; }
        public string? ExpectedNicknameForHumanReview { get; set; }
        public string Id { get; set; } = "";
        public string Question { get; set; } = "";
        public string Rubric { get; set; } = "";
        public string Context { get; set; } = "";
        public string ContextInspection { get; set; } = "";
        public string ContextSha256 { get; set; } = "";
        public string PromptTokenRange { get; set; } = "";
        public string ExecutionOutcome { get; set; } = "not_attempted";
        public double RequestDeadlineSeconds { get; set; }
        public string[] ExpectedFacts { get; set; } = Array.Empty<string>();
        public int ContextCharacters { get; set; }
        public int? WorkerProcessId { get; set; }
        public bool UnscoredWarmup { get; set; }
        public double WallMilliseconds { get; set; }
        public PdaQaReply? Reply { get; set; }
        public string? Error { get; set; }
    }
    public sealed record HelperProcess(int ProcessId, DateTimeOffset? StartedAtUtc);
    public sealed record MemorySample(double WallMilliseconds, int? ProcessId, long? WorkingSetBytes, long? PrivateBytes, long? OsPeakWorkingSetBytes,
        string? NvidiaDeviceWideCsv, string? NvidiaError, string Attribution = "NVIDIA figures are device-wide estimates, not exact model allocations", string Phase = "sample");

    internal sealed class ComparisonSampler(LocalPdaQaClient client, Stopwatch timer) : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly object _sync = new();
        private readonly Dictionary<int, Process> _owned = new();
        private readonly List<MemorySample> _samples = new();
        private Task? _loop;
        private bool _nvidiaAvailable = true;
        public IReadOnlyList<MemorySample> Samples { get { lock (_sync) return _samples.ToArray(); } }
        public IReadOnlyList<HelperProcess> Processes
        {
            get { lock (_sync) return _owned.Select(p => new HelperProcess(p.Key, StartTime(p.Value))).ToArray(); }
        }
        public void Start() => _loop = Task.Run(SampleLoop);
        public async Task CaptureBaselineAsync()
        {
            (string? csv, string? error) = await Nvidia();
            lock (_sync) _samples.Add(new MemorySample(timer.Elapsed.TotalMilliseconds, null, null, null, null, csv, error, Phase: "before helper startup"));
        }
        public void TrackCurrent()
        {
            int? id = client.WorkerProcessId;
            if (id == null) return;
            lock (_sync)
                if (!_owned.ContainsKey(id.Value))
                    try { _owned.Add(id.Value, Process.GetProcessById(id.Value)); } catch (ArgumentException) { }
        }
        private async Task SampleLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                TrackCurrent();
                int? id = client.WorkerProcessId;
                long? rss = null, priv = null, peak = null;
                lock (_sync)
                    if (id != null && _owned.TryGetValue(id.Value, out Process? p))
                        try { p.Refresh(); rss = p.WorkingSet64; priv = p.PrivateMemorySize64; peak = p.PeakWorkingSet64; } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                (string? csv, string? error) = await Nvidia();
                lock (_sync) if (_samples.Count < 2000) _samples.Add(new MemorySample(timer.Elapsed.TotalMilliseconds, id, rss, priv, peak, csv, error));
                try { await Task.Delay(500, _stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
        private async Task<(string?, string?)> Nvidia()
        {
            if (!_nvidiaAvailable) return (null, null);
            using var p = new Process { StartInfo = new ProcessStartInfo("nvidia-smi") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            try
            {
                p.StartInfo.ArgumentList.Add("--query-gpu=index,name,memory.used,memory.total,utilization.gpu");
                p.StartInfo.ArgumentList.Add("--format=csv,noheader,nounits");
                p.Start();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                Task<string> output = p.StandardOutput.ReadToEndAsync(timeout.Token), error = p.StandardError.ReadToEndAsync(timeout.Token);
                await p.WaitForExitAsync(timeout.Token);
                string text = await output, detail = await error;
                if (p.ExitCode != 0) { _nvidiaAvailable = false; return (null, detail.Trim()); }
                return (text.Trim(), null);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
            {
                try { if (!p.HasExited) p.Kill(true); } catch (Exception cleanup) when (cleanup is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                _nvidiaAvailable = false;
                return (null, e.GetType().Name);
            }
        }
        public async Task StopAsync()
        {
            _stop.Cancel();
            if (_loop != null) try { await _loop.WaitAsync(TimeSpan.FromSeconds(4)); }
                catch (Exception e) { lock (_sync) _samples.Add(new MemorySample(timer.Elapsed.TotalMilliseconds, null, null, null, null, null, "Sampler stopped: " + e.GetType().Name)); }
        }
        public async Task<bool> ConfirmExitAsync()
        {
            Process[] processes; lock (_sync) processes = _owned.Values.ToArray();
            foreach (Process p in processes)
                try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); await p.WaitForExitAsync(timeout.Token); }
                catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
            return true;
        }
        private static DateTimeOffset? StartTime(Process p) { try { return new DateTimeOffset(p.StartTime.ToUniversalTime()); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; } }
        public void Dispose() { foreach (Process p in _owned.Values) p.Dispose(); _stop.Dispose(); }
    }
}
