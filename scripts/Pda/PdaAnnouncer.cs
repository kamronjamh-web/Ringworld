using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;

namespace RingworldTechDemo.Pda;

/// <summary>
/// Minimal, non-event-specific PDA core (docs/eclipse-false-sun-spec.md F.3-F.4, docs/megastorm-design.md 5.4): the line request, the provider and
/// voice-output seams, the authored table and the announcer queue. No LLM and no TTS; the eclipse work adds its table, an LLM provider and voice
/// output on top of these interfaces.
/// </summary>
public readonly record struct PdaRequest(string Id, int Priority, bool Critical, string AuthoredLine,
    IReadOnlyDictionary<string, string> Facts, IReadOnlyList<string> RequiredTerms);

public interface IPdaLineProvider
{
    bool TryCompose(in PdaRequest request, CancellationToken token, out string line);
}

public interface IPdaVoiceOutput
{
    void Speak(string line, int priority);
    void Subtitle(string line);
}

/// <summary>One authored table row (the prototypes/pda-voice/events.json schema): priority 0 is critical and skips any rewrite.</summary>
public sealed record PdaLineDefinition(string Id, string Category, int Priority, string Fallback, IReadOnlyDictionary<string, string> Facts, IReadOnlyList<string> Terms);

public sealed class PdaLineTable
{
    private readonly Dictionary<string, PdaLineDefinition> _lines = new();

    public IReadOnlyCollection<PdaLineDefinition> Lines => _lines.Values;
    public bool TryGet(string id, out PdaLineDefinition definition) => _lines.TryGetValue(id, out definition!);

    public static PdaLineTable Parse(string json)
    {
        var table = new PdaLineTable();
        using JsonDocument doc = JsonDocument.Parse(json);
        foreach (JsonElement e in doc.RootElement.GetProperty("events").EnumerateArray())
        {
            var facts = new Dictionary<string, string>();
            if (e.TryGetProperty("facts", out JsonElement f))
                foreach (JsonProperty p in f.EnumerateObject()) facts[p.Name] = FactText(p.Value);
            var terms = new List<string>();
            if (e.TryGetProperty("terms", out JsonElement t))
                foreach (JsonElement term in t.EnumerateArray()) terms.Add(term.GetString() ?? string.Empty);
            string id = e.GetProperty("id").GetString() ?? string.Empty;
            table._lines[id] = new PdaLineDefinition(id, e.TryGetProperty("category", out JsonElement c) ? c.GetString() ?? string.Empty : string.Empty,
                e.GetProperty("priority").GetInt32(), e.GetProperty("fallback").GetString() ?? string.Empty, facts, terms);
        }
        return table;
    }

    private static string FactText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDouble().ToString("0.###", CultureInfo.InvariantCulture),
        JsonValueKind.String => v.GetString() ?? string.Empty,
        _ => v.ToString()
    };

    /// <summary>A request for <paramref name="id"/> with the given runtime facts (the table's example facts are only validator samples); false if the id is unknown.</summary>
    public bool TryCreate(string id, IReadOnlyDictionary<string, string> facts, out PdaRequest request)
    {
        if (!_lines.TryGetValue(id, out PdaLineDefinition? d)) { request = default; return false; }
        request = new PdaRequest(d.Id, d.Priority, d.Priority == 0, d.Fallback, facts, d.Terms);
        return true;
    }
}

/// <summary>Default provider: fills the authored template's {fact} placeholders.</summary>
public sealed class AuthoredPdaLineProvider : IPdaLineProvider
{
    public bool TryCompose(in PdaRequest request, CancellationToken token, out string line)
    {
        line = Fill(request.AuthoredLine, request.Facts);
        return line.Length > 0;
    }

    public static string Fill(string template, IReadOnlyDictionary<string, string> facts)
    {
        string line = template;
        foreach (KeyValuePair<string, string> kv in facts) line = line.Replace("{" + kv.Key + "}", kv.Value, StringComparison.Ordinal);
        return line;
    }
}

/// <summary>Subtitles now (the sink is the on-screen overlay); Kokoro or Piper speech later.</summary>
public sealed class SubtitleOnlyVoiceOutput : IPdaVoiceOutput
{
    private readonly Action<string> _sink;
    public SubtitleOnlyVoiceOutput(Action<string> sink) { _sink = sink; }
    public void Speak(string line, int priority) { }
    public void Subtitle(string line) => _sink(line);
}

/// <summary>
/// The announcer queue (eclipse spec F.3): order by priority then age, drop requests older than <see cref="MaxStaleSeconds"/>, 4 s between lines,
/// 60 s per id, critical lines (and a resync) speak at once and drop the queued non-critical backlog, and on a resync only that line is spoken.
/// Time comes from an injected clock so tests and the weather clock drive it.
/// </summary>
public sealed class PdaAnnouncer
{
    public const double MaxStaleSeconds = 8.0, CooldownSeconds = 4.0, PerIdSeconds = 60.0;

    private readonly Func<double> _clock;
    private readonly IPdaLineProvider _provider;
    private readonly IPdaLineProvider _authored = new AuthoredPdaLineProvider();
    private readonly IPdaVoiceOutput _output;
    private readonly List<(PdaRequest Request, double Time)> _queue = new();
    private readonly Dictionary<string, double> _lastSpokenById = new();
    private double _lastSpoken = double.NegativeInfinity;

    /// <summary>Master switch: false drops everything silently.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Subtitles always show when the announcer is enabled; this exists for the future settings screen.</summary>
    public bool Subtitles { get; set; } = true;
    public string? LastSpokenId { get; private set; }

    public PdaAnnouncer(Func<double> clock, IPdaLineProvider? provider, IPdaVoiceOutput output)
    {
        _clock = clock;
        _provider = provider ?? _authored;
        _output = output;
    }

    public int Pending => _queue.Count;

    /// <summary>Queues a request; a critical one pre-empts: the queued non-critical lines are dropped and it speaks immediately.</summary>
    public void Submit(in PdaRequest request)
    {
        if (!Enabled) return;
        double now = _clock();
        if (!request.Critical) { _queue.Add((request, now)); return; }
        _queue.RemoveAll(q => !q.Request.Critical);
        Speak(request, now, ignorePerId: false);
    }

    /// <summary>Resync after a load or jump into an event: the backlog is dropped and only this line is spoken.</summary>
    public void SubmitResync(in PdaRequest request)
    {
        if (!Enabled) return;
        _queue.Clear();
        Speak(request, _clock(), ignorePerId: true);
    }

    /// <summary>Speaks the best queued line when the cooldown allows it. Call every frame.</summary>
    public void Tick()
    {
        if (!Enabled || _queue.Count == 0) return;
        double now = _clock();
        _queue.RemoveAll(q => now - q.Time > MaxStaleSeconds);
        if (_queue.Count == 0 || now - _lastSpoken < CooldownSeconds) return;
        int best = 0;
        for (int i = 1; i < _queue.Count; i++)
            if (_queue[i].Request.Priority < _queue[best].Request.Priority) best = i;
        PdaRequest request = _queue[best].Request;
        _queue.RemoveAt(best);
        Speak(request, now, ignorePerId: false);
    }

    private void Speak(in PdaRequest request, double now, bool ignorePerId)
    {
        if (!ignorePerId && _lastSpokenById.TryGetValue(request.Id, out double last) && now - last < PerIdSeconds) return;
        IPdaLineProvider provider = request.Critical ? _authored : _provider;
        if (!provider.TryCompose(request, CancellationToken.None, out string line) && !_authored.TryCompose(request, CancellationToken.None, out line)) return;
        _lastSpoken = now;
        _lastSpokenById[request.Id] = now;
        LastSpokenId = request.Id;
        _output.Speak(line, request.Priority);
        if (Subtitles) _output.Subtitle(line);
    }
}

/// <summary>A PDA database entry (resources/pda/*_database.json): a title and its paragraphs.</summary>
public sealed record PdaDatabaseEntry(string Id, string Title, string UnlockedBy, IReadOnlyList<string> Paragraphs)
{
    public static PdaDatabaseEntry Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement e = doc.RootElement.GetProperty("entry");
        var paragraphs = new List<string>();
        foreach (JsonElement p in e.GetProperty("paragraphs").EnumerateArray()) paragraphs.Add(p.GetString() ?? string.Empty);
        return new PdaDatabaseEntry(e.GetProperty("id").GetString() ?? string.Empty, e.GetProperty("title").GetString() ?? string.Empty,
            e.TryGetProperty("unlocked_by", out JsonElement u) ? u.GetString() ?? string.Empty : string.Empty, paragraphs);
    }
}
