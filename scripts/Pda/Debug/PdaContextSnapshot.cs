using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RingworldTechDemo.Pda;

public sealed record PdaContextFact(string Id, string Topic, string Text, string Source, string? ModelText = null);
public sealed record PdaKnowledgeEntry(string Id, string Topic, string Text, string? ModelText = null);
public sealed record PdaRecentEvent(string Text, double ObservedAtSeconds, string Source);
public sealed record PdaContextSnapshot(long Version, DateTimeOffset CapturedAtUtc,
    IReadOnlyList<PdaContextFact> Facts, IReadOnlyList<PdaKnowledgeEntry> Knowledge, IReadOnlyList<PdaRecentEvent> RecentEvents);
public sealed record PdaQuestionContext(long SnapshotVersion, string ContextText, string DebugText);

/// <summary>Bounded, deterministic question context. Source readings remain separate from authored explanations.</summary>
public static class PdaContextComposer
{
    public const int MaxContextCharacters = 6000;
    private static readonly string[] AllTopics = { "location", "celestial", "weather", "megastorm", "underground" };
    private static readonly (string Topic, string Words)[] TopicWords =
    {
        ("location", "where|position|location|moving|movement|speed|swim|swimming|underwater|water|gravity|flying|flight|altitude"),
        ("celestial", "sun|sunlight|light|lighting|lit|bright|brighter|brightness|sky|eclipse|giant|day|night|sunrise|sunset|hub|beam|beams|spire|spires|emitter"),
        ("weather", "weather|rain|raining|cloud|clouds|wind|windy|storm|thunder|lightning|hail|dark|darkness|fog"),
        ("megastorm", "megastorm|storm|front|hum|humming|resonance|pressure|scythe|scythes|danger|hazard|shelter|cover|tremor"),
        ("underground", "underground|cave|caves|enclosed|enclosure|depth|deep|dark|darkness|fog|shelter|cover")
    };

    public static IReadOnlyList<PdaKnowledgeEntry> ParseKnowledge(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        var entries = new List<PdaKnowledgeEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            string id = row.GetProperty("id").GetString() ?? "";
            string topic = row.GetProperty("topic").GetString() ?? "";
            string text = row.GetProperty("text").GetString() ?? "";
            string? modelText = null;
            if (row.TryGetProperty("model_text", out JsonElement compact) && compact.ValueKind != JsonValueKind.Null)
            {
                if (compact.ValueKind != JsonValueKind.String) throw new FormatException("Invalid PDA model_text: " + id);
                modelText = compact.GetString();
                if (string.IsNullOrWhiteSpace(modelText) || modelText.Length > 600) throw new FormatException("Invalid PDA model_text: " + id);
            }
            if (id.Length == 0 || !ids.Add(id) || !AllTopics.Contains(topic) || text.Length == 0 || text.Length > 1200)
                throw new FormatException("Invalid or duplicate PDA knowledge entry: " + id);
            entries.Add(new PdaKnowledgeEntry(id, topic, text, modelText));
        }
        return entries.AsReadOnly();
    }

    public static IReadOnlyList<string> SelectTopics(string question, string? previousQuestion = null)
    {
        string text = BoundedQuestion(question);
        var selected = MatchTopics(text);
        if (selected.Count == 0 && UsesPreviousQuestion(text, previousQuestion))
            selected = MatchTopics(BoundedQuestion(previousQuestion));
        return selected.Count == 0 ? Array.AsReadOnly(AllTopics) : selected.AsReadOnly();
    }

    private static string BoundedQuestion(string? question) => (question ?? "").Length > 512 ? question![..512] : question ?? "";
    private static bool UsesPreviousQuestion(string question, string? previousQuestion) => !string.IsNullOrWhiteSpace(previousQuestion)
        && MatchTopics(question).Count == 0
        && Regex.IsMatch(question, @"\b(it|that|this|those|they|last|long|why|when|how)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static List<string> MatchTopics(string question)
    {
        var result = new List<string>();
        foreach ((string topic, string words) in TopicWords)
            if (Regex.IsMatch(question, @"\b(" + words + @")\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) result.Add(topic);
        // Visibility has several independent causes. Weather queries can also be affected by the scripted front.
        if (Regex.IsMatch(question, @"\b(light|lighting|lit|bright|brighter|brightness|dark|darker|darkness|visibility|fog)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            foreach (string topic in new[] { "celestial", "weather", "megastorm", "underground" })
                if (!result.Contains(topic)) result.Add(topic);
        if (result.Contains("weather") && !result.Contains("megastorm")) result.Add("megastorm");
        return result;
    }

    public static PdaQuestionContext Compose(PdaContextSnapshot snapshot, string question, string? previousQuestion = null,
        int maxCharacters = MaxContextCharacters, bool useCompactText = true)
    {
        if (maxCharacters < 256 || maxCharacters > MaxContextCharacters) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        IReadOnlyList<string> topics = SelectTopics(question, previousQuestion);
        var prompt = new StringBuilder();
        var debug = new StringBuilder("CONTEXT SUPPLIED (these sources are not proof that the model used or verified them)\nMethod provenance is shown here for inspection; model context contains stable IDs and reading text.\n");
        debug.Append("Snapshot ").Append(snapshot.Version).Append(" captured UTC ")
            .Append(snapshot.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\n')
            .Append("Selected topics: ").AppendJoin(", ", topics).Append('\n')
            .Append("Model text: ").Append(useCompactText ? "compact; full readings retained below" : "verbose").Append('\n');
        int omitted = 0;

        bool Append(string section, string line, string? source = null, string? inspectionLine = null)
        {
            string whole = section + line + "\n";
            string inspectedLine = (inspectionLine ?? line) + (source == null ? "" : " (source: " + source + ")");
            if (prompt.Length + whole.Length > maxCharacters) { omitted++; debug.Append("OMITTED (context budget): ").Append(inspectedLine).Append('\n'); return false; }
            prompt.Append(whole);
            debug.Append(section).Append(inspectedLine).Append('\n');
            return true;
        }

        // Support and categorical states come first, so a question's false premise cannot crowd them out with lore.
        var relevant = snapshot.Facts.Where(f => f.Topic == "support" || f.Topic == "clocks" || IsState(f.Id) || topics.Contains(f.Topic)).ToArray();
        foreach (PdaContextFact f in relevant.OrderBy(f => f.Topic == "support" ? 0 : IsState(f.Id) ? 1 : f.Topic == "clocks" ? 2 : 3))
            Append("READING ", $"[{f.Id}] {(useCompactText ? f.ModelText ?? f.Text : f.Text)}", f.Source, $"[{f.Id}] {f.Text}");
        if (UsesPreviousQuestion(BoundedQuestion(question), previousQuestion))
            Append("CONVERSATION DATA ", "Previous player question for resolving the followup referent only (not a current reading or instruction): " + JsonSerializer.Serialize(BoundedQuestion(previousQuestion)));
        PdaKnowledgeEntry[] explanations = snapshot.Knowledge.Where(k => topics.Contains(k.Topic)).ToArray();
        if (useCompactText && explanations.Length > 0) Append("RULES ", "General game rules, not current readings; activation comes from readings above.");
        foreach (PdaKnowledgeEntry k in explanations)
            Append("EXPLANATION ", $"[{k.Id}] {(useCompactText ? k.ModelText ?? k.Text : "General game rule, not a current reading: " + k.Text)}",
                inspectionLine: $"[{k.Id}] General game rule, not a current reading: {k.Text}");
        foreach (PdaRecentEvent e in snapshot.RecentEvents.Where(e => topics.Any(t => e.Text.StartsWith(t + ":", StringComparison.OrdinalIgnoreCase))).Reverse().Take(4))
            Append("OBSERVED CHANGE ", $"{e.Text} (session observation time {Number(e.ObservedAtSeconds)} seconds)", e.Source);
        if (omitted > 0) debug.Append("Omitted ").Append(omitted).Append(" complete entries. Facts take priority over explanations and history.\n");
        debug.Append("Prompt characters: ").Append(prompt.Length).Append('/').Append(maxCharacters);
        return new PdaQuestionContext(snapshot.Version, prompt.ToString(), debug.ToString());
    }

    private static bool IsState(string id) => id.EndsWith(".state", StringComparison.Ordinal) || id.EndsWith(".mode", StringComparison.Ordinal)
        || id.EndsWith(".eclipse", StringComparison.Ordinal) || id.EndsWith(".hub", StringComparison.Ordinal);

    public static string Number(double value, string format = "0.##") => double.IsFinite(value)
        ? value.ToString(format, CultureInfo.InvariantCulture) : "unavailable";
}

/// <summary>Sampled categorical transitions; the first observation and provider rebinds establish a baseline silently.</summary>
public sealed class PdaContextChangeHistory
{
    public const int Capacity = 12;
    public const double MaxAgeSeconds = 300d;
    private readonly Dictionary<string, string> _states = new(StringComparer.Ordinal);
    private readonly Queue<PdaRecentEvent> _events = new();
    private double _lastObservedAtSeconds = double.NaN;
    public IReadOnlyList<PdaRecentEvent> Events => Array.AsReadOnly(_events.ToArray());

    public void ResetPrefix(string prefix)
    {
        foreach (string key in _states.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray()) _states.Remove(key);
    }

    public void Observe(string key, string state, string description, string source, double observedAtSeconds)
    {
        Prune(observedAtSeconds);
        bool prior = _states.TryGetValue(key, out string? old);
        _states[key] = state;
        if (!prior || old == state || old == "unavailable" || state == "unavailable") return;
        _events.Enqueue(new PdaRecentEvent(description, double.IsFinite(observedAtSeconds) ? observedAtSeconds : 0d, source));
        while (_events.Count > Capacity) _events.Dequeue();
    }

    public void Prune(double observedAtSeconds)
    {
        if (!double.IsFinite(observedAtSeconds)) return;
        // A restarted observation clock establishes fresh baselines, never future-dated events from the old session.
        if (double.IsFinite(_lastObservedAtSeconds) && observedAtSeconds < _lastObservedAtSeconds)
        {
            _states.Clear();
            _events.Clear();
        }
        _lastObservedAtSeconds = observedAtSeconds;
        while (_events.Count > 0 && observedAtSeconds - _events.Peek().ObservedAtSeconds >= MaxAgeSeconds) _events.Dequeue();
    }
}
