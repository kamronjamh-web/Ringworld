using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PdaVoicePrototype;

public sealed class PdaEvent
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public int Priority { get; set; }
    public string Fallback { get; set; } = "";
    public Dictionary<string, JsonElement> Facts { get; set; } = new();
    public List<string> Terms { get; set; } = new();

    public IEnumerable<string> NumericFacts =>
        Facts.Values.Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetRawText());

    public string FactsText => string.Join("; ", Facts.Select(f => $"{f.Key.Replace('_', ' ')}: {f.Value.ToString().ToLowerInvariant()}"));
}

public static class Paths
{
    // Resolve the prototype directory by walking up from the exe until models/events.json siblings are found.
    public static readonly string Root = FindRoot();
    public static string Models => Path.Combine(Root, "models");
    public static string Output => Path.Combine(Root, "output");

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PdaVoicePrototype.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate prototypes/pda-voice from " + AppContext.BaseDirectory);
    }
}

public static class Events
{
    static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static List<PdaEvent> Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Paths.Root, "events.json")));
        return JsonSerializer.Deserialize<List<PdaEvent>>(doc.RootElement.GetProperty("events").GetRawText(), Opts)!;
    }
}

public static class Validator
{
    const int MaxWords = 25;
    static readonly Dictionary<string, string> NumberWords = new()
    {
        ["0"] = "zero", ["1"] = "one", ["2"] = "two", ["3"] = "three", ["4"] = "four", ["5"] = "five", ["6"] = "six",
        ["7"] = "seven", ["8"] = "eight", ["9"] = "nine", ["10"] = "ten", ["12"] = "twelve", ["15"] = "fifteen", ["20"] = "twenty",
    };

    /// <summary>Returns null when the line is acceptable, otherwise the rejection reason.</summary>
    public static string? Check(PdaEvent ev, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return "empty";
        if (text.Contains('\n') || text.Contains('\r')) return "multiline";
        if (Regex.IsMatch(text, @"[*#`<>\[\]{}_|~\\]|[^\x20-\x7E]")) return "markup/non-ascii";
        if (Regex.IsMatch(text, @"</?think>|im_start|im_end", RegexOptions.IgnoreCase)) return "markup/non-ascii";
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words > MaxWords) return $"too long ({words} words)";
        if (!(text.EndsWith('.') || text.EndsWith('!'))) return "no terminal punctuation (truncated)";
        var lower = text.ToLowerInvariant();
        // Prompt/example leakage and raw fact-key echoes ("output pct: 0", "first time true").
        foreach (var banned in new[] { "authored", "fact", "hull", "breach", "sector", "seal", " pct", "true", "false" })
            if (Regex.IsMatch(lower, $@"\b{banned.Trim()}")) return $"prompt leakage ({banned.Trim()})";
        foreach (var n in ev.NumericFacts)
        {
            var pattern = $@"(?<![0-9.]){Regex.Escape(n)}(?![0-9])";
            var ok = Regex.IsMatch(lower, pattern);
            if (!ok && NumberWords.TryGetValue(n, out var word)) ok = Regex.IsMatch(lower, $@"\b{word}\b");
            if (!ok) return $"dropped numeric fact {n}";
        }
        foreach (var t in ev.Terms)
            if (!lower.Contains(t)) return $"dropped term '{t}'";
        return null;
    }
}

public static class Stats
{
    public static double Percentile(IList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var s = values.OrderBy(v => v).ToList();
        var rank = p * (s.Count - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return s[lo] + (s[hi] - s[lo]) * (rank - lo);
    }
}

public static class Wav
{
    public static void Write16(string path, float[] samples, int sampleRate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        int dataBytes = samples.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataBytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataBytes);
        foreach (var s in samples) w.Write((short)Math.Clamp(MathF.Round(s * 32767f), -32768, 32767));
    }

    /// <summary>Reads a PCM16 or float32 WAV (first channel only).</summary>
    public static (float[] Samples, int SampleRate) Read(byte[] bytes)
    {
        using var r = new BinaryReader(new MemoryStream(bytes));
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") throw new InvalidDataException("not RIFF");
        r.ReadInt32(); r.ReadBytes(4);
        int fmtTag = 1, channels = 1, rate = 0, bits = 16;
        while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
        {
            var id = Encoding.ASCII.GetString(r.ReadBytes(4));
            int size = r.ReadInt32();
            if (id == "fmt ")
            {
                fmtTag = r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                r.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                size = (int)Math.Min(size, r.BaseStream.Length - r.BaseStream.Position);
                int bytesPer = bits / 8;
                int frames = size / (bytesPer * channels);
                var outS = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    float v = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        float s = fmtTag == 3 ? r.ReadSingle() : bits == 16 ? r.ReadInt16() / 32768f : throw new NotSupportedException($"{bits}-bit wav");
                        if (c == 0) v = s;
                    }
                    outS[i] = v;
                }
                return (outS, rate);
            }
            else r.ReadBytes(size);
        }
        throw new InvalidDataException("no data chunk");
    }
}

public static class Fmt
{
    public static string F(double v, int d = 1) => v.ToString("F" + d, CultureInfo.InvariantCulture);
}
