using System.Text;
using System.Text.Json;

namespace PdaVoicePrototype;

public static class LlmReport
{
    public static void Write(List<WorkerResult> results, List<PdaEvent> events)
    {
        File.WriteAllText(Path.Combine(Paths.Output, "llm_results.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

        var sb = new StringBuilder();
        sb.AppendLine("# PDA announcer LLM results");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}. CPU only (0 GPU layers), {LlmBench.Samples} samples per event, {events.Count} events, temperature {LlmBench.Temperature}, max {LlmBench.MaxTokens} tokens, GBNF-constrained single-line output. Each row is a separate process (clean RAM numbers). Latency is for the whole rewrite (prompt + generation, stateless, no KV reuse).");
        sb.AppendLine();
        sb.AppendLine("| Model | Threads | Loaded | Load s | RAM after load MB | Peak RAM MB | Tokens/s (gen) | TTFT ms p50 | Latency ms p50 | Latency ms p95 | Reject rate |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            if (!r.Loaded) { sb.AppendLine($"| {r.Model} | {r.Threads} | NO ({r.Error}) | | | | | | | | |"); continue; }
            var lat = r.Lines.Select(l => l.LatencyMs).ToList();
            var ttft = r.Lines.Select(l => l.FirstTokenMs).ToList();
            var tps = r.Lines.Where(l => l.TokensPerSecond > 0).Select(l => l.TokensPerSecond).Average();
            var rej = r.Lines.Count(l => l.Rejected);
            sb.AppendLine($"| {r.Model} | {r.Threads} | yes | {Fmt.F(r.LoadSeconds, 2)} | {Fmt.F(r.WorkingSetAfterLoadMb, 0)} | {Fmt.F(r.PeakWorkingSetMb, 0)} | {Fmt.F(tps)} | {Fmt.F(Stats.Percentile(ttft, .5), 0)} | {Fmt.F(Stats.Percentile(lat, .5), 0)} | {Fmt.F(Stats.Percentile(lat, .95), 0)} | {rej}/{r.Lines.Count} ({Fmt.F(100.0 * rej / r.Lines.Count, 0)}%) |");
        }

        sb.AppendLine();
        sb.AppendLine("## Reject reasons");
        foreach (var r in results.Where(r => r.Loaded))
        {
            var groups = r.Lines.Where(l => l.Rejected)
                .GroupBy(l => System.Text.RegularExpressions.Regex.Replace(l.RejectReason!, @"\d+", "N"))
                .OrderByDescending(g => g.Count()).Select(g => $"{g.Key} x{g.Count()}");
            sb.AppendLine($"- {r.Model} @ {r.Threads} threads: {(groups.Any() ? string.Join("; ", groups) : "none")}");
        }

        var primary = results.Where(r => r.Loaded).GroupBy(r => r.Model).Select(g => g.OrderByDescending(r => r.Threads).First()).ToList();
        sb.AppendLine();
        sb.AppendLine($"## Every line, side by side (first thread count: {string.Join(", ", primary.Select(p => $"{p.Model}@{p.Threads}"))})");
        foreach (var ev in events)
        {
            sb.AppendLine();
            sb.AppendLine($"### {ev.Id}  (priority {ev.Priority}, {ev.Category})");
            sb.AppendLine($"- authored: {ev.Fallback}");
            foreach (var p in primary)
                foreach (var l in p.Lines.Where(l => l.EventId == ev.Id))
                    sb.AppendLine($"- {p.Model} #{l.Sample + 1}: {(l.Raw.Length == 0 ? "(empty)" : l.Raw)}{(l.Rejected ? $"   [REJECT: {l.RejectReason}]" : "")}");
        }
        File.WriteAllText(Path.Combine(Paths.Output, "llm_results.md"), sb.ToString());
        Console.WriteLine(sb.ToString().Split("## Every line")[0]);
    }
}
