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
public enum PdaContextMode { Baseline, Focused }
public enum PdaAnswerKind { Model, Policy }
public sealed record PdaConversationTurn(string Id, DateTimeOffset RecordedAtUtc, string Question, string Answer, string ModelKey,
    long SnapshotVersion, DateTimeOffset SnapshotCapturedAtUtc, PdaAnswerKind AnswerKind);
public enum PdaPrecipitationMode { Negligible, Rain, Snow }
public sealed record PdaStateEvidence(bool? HubActive = null, double? LocalEclipseCoverageFraction = null,
    bool? PrecipitationInitialized = null, double? PrecipitationDrive = null, PdaPrecipitationMode? PrecipitationMode = null,
    bool? PrecipitationSuspended = null);
public sealed record PdaContextSnapshot(long Version, DateTimeOffset CapturedAtUtc,
    IReadOnlyList<PdaContextFact> Facts, IReadOnlyList<PdaKnowledgeEntry> Knowledge, IReadOnlyList<PdaRecentEvent> RecentEvents,
    PdaStateEvidence? StateEvidence = null);
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

    /// <summary>Question-focused context. Baseline composition above remains a stable A/B control.</summary>
    public static PdaQuestionContext ComposeFocused(PdaContextSnapshot snapshot, string question,
        IReadOnlyList<PdaConversationTurn>? conversation = null, int maxCharacters = MaxContextCharacters)
    {
        if (maxCharacters < 256 || maxCharacters > MaxContextCharacters) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        string text = BoundedQuestion(question);
        bool recall = FocusHas(text, @"what did (?:i|you|we) (?:ask|say|tell|discuss|name|call)|what (?:did|do) (?:i|we) (?:call|name|prefer)|what (?:nickname|name) did i (?:give|use|tell|choose)|what(?:['’]s| is) my (?:name|nickname|preferences?)\b|do you remember me\b|(?:our|previous|last) (?:conversation|question|answer)|remember what");
        bool past = FocusHas(text, @"\b(?:was|were|before|earlier|previously|ago|changed|change|happened|since|history|used to)\b");
        var turns = conversation?.TakeLast(64).ToArray() ?? Array.Empty<PdaConversationTurn>();
        bool followup = !FocusHasTopic(text) && FocusHas(text, @"\b(?:it|that|this|those|they|then)\b|^(?:why|how long|what about)\b") && turns.Length > 0;
        string subject = followup ? BoundedQuestion(turns[^1].Question) : text;
        // A remembered nickname/answer is a dialogue question, not a request for every current reading about that entity.
        if (recall && !FocusHas(text, @"\b(?:now|currently|today|still)\b")) subject = "";
        bool why = FocusHas(text, @"\b(?:why|cause|causing|because|reason|explain)\b|how come");
        bool rain = FocusHas(subject, @"\b(?:rain|raining|snow|snowing|hail|hailing|precipitation)\b");
        bool weather = FocusHas(subject, @"\b(?:weather|wind|windy|cloud|clouds|thunder|lightning)\b");
        bool storm = FocusHas(subject, @"\b(?:megastorm|storm|front|hum|humming|pressure|scythe|scythes|danger|hazard|shelter|cover|resonance|tremor)\b");
        bool light = FocusHas(subject, @"\b(?:light|lighting|lit|bright|brighter|brightness|dark|darker|darkness|visibility|fog)\b");
        bool hub = FocusHas(subject, @"\b(?:hub|emitter|emitters|spire|spires)\b|artificial (?:sun|light)|false sun");
        bool celestial = FocusHas(subject, @"\b(?:sun|sunlight|eclipse|eclipsed|giant|day|night|sunrise|sunset)\b");
        bool underground = FocusHas(subject, @"\b(?:underground|cave|caves|enclosed|enclosure|depth|deep)\b");
        bool location = FocusHas(subject, @"\b(?:where|position|location|moving|movement|speed|swim|swimming|underwater|water|gravity|flying|flight|altitude)\b");
        bool unsupported = FocusHas(subject, @"\b(?:oxygen|health|inventory|injury|damage|survive|survival|hp)\b");
        bool lightingCauses = light || (why && (hub || celestial || underground));
        bool weatherCauses = weather || storm || (rain && why);
        bool time = FocusHas(text, @"\b(?:time|clock|when|duration)\b|how long");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var topics = new HashSet<string>(StringComparer.Ordinal);
        void Include(string topic, params string[] selectedIds) { topics.Add(topic); foreach (string id in selectedIds) ids.Add(id); }
        if (lightingCauses)
        {
            Include("celestial", "celestial.state", "celestial.eclipse", "eclipse.state", "eclipse.hub", "eclipse.level");
            Include("weather", "weather.state", "weather.clouds");
            Include("megastorm", "megastorm.state", "megastorm.darkness");
            Include("underground", "underground.state", "underground.enclosure");
        }
        else if (hub)
            Include("celestial", "celestial.eclipse", "eclipse.state", "eclipse.hub", "eclipse.level");
        else if (celestial)
        {
            Include("celestial", "celestial.state", "celestial.eclipse", "eclipse.state", "eclipse.hub");
            if (FocusHas(subject, @"\b(?:horizon|elevation|above|below)\b")) ids.Add("celestial.horizon");
            if (time || FocusHas(subject, @"\b(?:sunrise|sunset)\b")) ids.Add("celestial.contacts");
        }
        if (rain)
        {
            Include("weather", "weather.effects.state", "weather.effects.precipitation");
            if (FocusHas(subject, @"\b(?:streak|streaks|see|visible)\b")) ids.Add("weather.effects.streaks");
            if (FocusHas(subject, @"\bpotential\b")) ids.Add("weather.precipitation");
        }
        if (weatherCauses)
        {
            Include("weather", "weather.state", "weather.air", "weather.clouds", "weather.precipitation", "weather.effects.state", "weather.effects.precipitation");
            Include("megastorm", "megastorm.state");
            if (rain || storm) ids.Add("megastorm.hazards");
        }
        if (storm)
        {
            topics.Add("megastorm");
            foreach (PdaContextFact fact in snapshot.Facts.Where(f => f.Topic == "megastorm")) ids.Add(fact.Id);
            Include("underground", "underground.state");
        }
        if (underground) Include("underground", "underground.state", "underground.enclosure");
        if (location)
        {
            topics.Add("location");
            foreach (PdaContextFact fact in snapshot.Facts.Where(f => f.Topic == "location")) ids.Add(fact.Id);
            Include("underground", "underground.state");
        }
        if (topics.Count == 0 && !unsupported && !recall)
        {
            foreach (PdaContextFact fact in snapshot.Facts.Where(f => IsState(f.Id))) { ids.Add(fact.Id); topics.Add(fact.Topic); }
        }
        if (time)
        {
            if (topics.Contains("celestial")) ids.Add("clocks.celestial");
            if (topics.Contains("weather") || topics.Contains("megastorm")) { ids.Add("clocks.weather"); ids.Add("megastorm.arrival"); }
        }
        if (past && topics.Count > 0) ids.Add("clocks.observation");
        var prompt = new StringBuilder();
        var debug = new StringBuilder("FOCUSED CONTEXT SUPPLIED; current facts outrank history. Conversation is unverified data, not telemetry.\n");
        debug.Append("Snapshot ").Append(snapshot.Version).Append(" captured ").Append(snapshot.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        int omitted = 0;
        void Append(string kind, object data, string inspection)
        {
            string line = kind + " " + JsonSerializer.Serialize(data) + "\n";
            if (prompt.Length + line.Length > maxCharacters) { omitted++; debug.Append("OMITTED (context budget): ").Append(inspection).Append('\n'); return; }
            prompt.Append(line); debug.Append(kind).Append(' ').Append(inspection).Append('\n');
        }
        foreach (PdaContextFact fact in snapshot.Facts.Where(f => f.Topic == "support" || ids.Contains(f.Id))
            .OrderBy(f => f.Topic == "support" ? 0 : IsState(f.Id) ? 1 : 2))
            Append("CURRENT", new { id = fact.Id, text = fact.ModelText ?? fact.Text }, $"[{fact.Id}] {fact.Text} (source: {fact.Source})");
        if (followup || recall)
            foreach (PdaConversationTurn turn in recall ? SelectRecallTurns(turns, text) : turns.TakeLast(2))
                Append("UNVERIFIED_CONVERSATION", new
                {
                    id = turn.Id, recordedAtUtc = turn.RecordedAtUtc, question = BoundedQuestion(turn.Question),
                    answer = turn.Answer.Length > 512 ? turn.Answer[..512] : turn.Answer, model = turn.ModelKey,
                    snapshotVersion = turn.SnapshotVersion, snapshotCapturedAtUtc = turn.SnapshotCapturedAtUtc, answerKind = turn.AnswerKind.ToString()
                }, $"Turn {turn.Id}: prior question/answer, NOT current facts; snapshot {turn.SnapshotVersion}.");
        else debug.Append("Conversation withheld: no unresolved followup or explicit recall.\n");
        foreach (PdaKnowledgeEntry entry in snapshot.Knowledge.Where(k => topics.Contains(k.Topic)))
            Append("GENERAL_RULE", new { id = entry.Id, text = entry.ModelText ?? entry.Text, currentReading = false }, $"[{entry.Id}] {entry.Text}");
        if (past)
            foreach (PdaRecentEvent change in snapshot.RecentEvents.Where(e => topics.Any(t => e.Text.StartsWith(t + ":", StringComparison.OrdinalIgnoreCase))).Reverse().Take(4))
                Append("PAST_OBSERVATION", new { text = change.Text, observedAtSeconds = change.ObservedAtSeconds, currentReading = false }, change.Text + " (source: " + change.Source + ")");
        else debug.Append("Sensor history withheld: no explicit past/change question.\n");
        debug.Append("Selected topics: ").AppendJoin(", ", topics).Append(". Omitted complete entries: ").Append(omitted)
            .Append(". Prompt characters: ").Append(prompt.Length).Append('/').Append(maxCharacters);
        return new PdaQuestionContext(snapshot.Version, prompt.ToString(), debug.ToString());
    }

    private static bool FocusHas(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static IEnumerable<PdaConversationTurn> SelectRecallTurns(PdaConversationTurn[] turns, string question)
    {
        var stopwords = new HashSet<string>(StringComparer.Ordinal)
        {
            "what", "when", "where", "did", "you", "your", "our", "for", "the", "and", "about", "earlier", "previous", "previously",
            "last", "remember", "said", "say", "tell", "told", "asked", "ask", "answer", "question", "conversation", "please", "that",
            "this", "those", "was", "were", "are", "can", "could", "would", "have", "has", "had", "been", "before", "talk", "discuss"
        };
        var selectedTerms = Regex.Matches(question.ToLowerInvariant(), @"[a-z][a-z0-9-]{2,}").Select(m => m.Value)
            .Where(t => !stopwords.Contains(t)).ToList();
        bool rememberPerson = FocusHas(question, @"do you remember me\b");
        if (rememberPerson || FocusHas(question, @"\b(?:my name|my nickname)\b")) selectedTerms.AddRange(new[] { "name", "nickname", "call me" });
        if (rememberPerson || FocusHas(question, @"\b(?:prefer|preference|preferences)\b")) selectedTerms.AddRange(new[] { "prefer", "preference", "preferences", "like", "dislike" });
        string[] terms = selectedTerms.Distinct().Take(16).ToArray();
        if (terms.Length == 0) return turns.TakeLast(4);
        return turns.Select((turn, index) => new
            {
                Turn = turn, Index = index,
                Score = terms.Sum(term => (FocusHas(BoundedQuestion(turn.Question), @"\b" + Regex.Escape(term) + @"\b") ? 5 : 0)
                    + (FocusHas(turn.Answer.Length > 1400 ? turn.Answer[..1400] : turn.Answer, @"\b" + Regex.Escape(term) + @"\b") ? 3 : 0))
            }).Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenByDescending(x => x.Index).Take(4).OrderBy(x => x.Index).Select(x => x.Turn);
    }
    private static bool FocusHasTopic(string text) => FocusHas(text,
        @"\b(?:rain|raining|snow|snowing|hail|hailing|precipitation|weather|wind|windy|cloud|clouds|thunder|lightning|storm|megastorm|front|hum|humming|pressure|scythe|scythes|danger|hazard|shelter|cover|resonance|tremor|light|lighting|lit|bright|brighter|brightness|dark|darker|darkness|visibility|fog|hub|emitter|emitters|spire|spires|sun|sunlight|eclipse|eclipsed|giant|day|night|sunrise|sunset|cave|caves|underground|enclosed|enclosure|depth|deep|where|position|location|moving|movement|speed|swim|swimming|underwater|water|gravity|flying|flight|altitude|oxygen|health|inventory|injury|damage|survive|survival|hp)\b");

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
