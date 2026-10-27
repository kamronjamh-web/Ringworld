using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PdaVoicePrototype;

public static partial class VoiceAudition
{
    const string ExpressiveModelRevision = "5ecdb67327fd37bb2e042aab12ff7391903235d3";
    const string ExpressiveCodeRevision = "022e286b98fbec7e1e916cb940cdf532cd9f488e";
    sealed record ExpressiveLine(string EventId, string Text, string RawPath, Levels RawLevels, List<EffectClip> Effects);
    sealed record ExpressiveStyle(string StyleId, string Label, List<ExpressiveLine> Lines, List<EffectReel> Reels);
    sealed record EmmaReference(string EffectId, string Label, string Path);

    public static int RunExpressiveReport(string[] args)
    {
        try
        {
            bool dialogueR1 = args.Length == 1 && args[0] == "--dialogue-r1";
            if (args.Length != 0 && !dialogueR1) throw new ArgumentException("audition-expressive-report accepts only --dialogue-r1.");
            string configFile = dialogueR1 ? "expressive-dialogue-r1.json" : "expressive-prompts.json";
            var output = Path.Combine(Paths.Output, dialogueR1 ? "dialogue-r1" : "expressive");
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "raw_manifest.json")));
            var raw = document.RootElement;
            if (!raw.GetProperty("Complete").GetBoolean() || raw.GetProperty("Smoke").GetBoolean()
                || raw.GetProperty("ModelId").GetString() != "Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign"
                || raw.GetProperty("ModelRevision").GetString() != ExpressiveModelRevision
                || raw.GetProperty("CodeRevision").GetString() != ExpressiveCodeRevision)
                throw new InvalidDataException("A completed full audition from the pinned expressive model/code is required.");
            if (dialogueR1 && (raw.GetProperty("DialogueRevision").GetString() != "dialogue-r1" || raw.GetProperty("ConfigFile").GetString() != configFile))
                throw new InvalidDataException("Expected dialogue-r1 revision and config metadata.");
            using var promptDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(Paths.Root, configFile)));
            var prompts = promptDocument.RootElement;
            if (raw.GetProperty("Persona").GetString() != prompts.GetProperty("Persona").GetString()
                || !JsonElement.DeepEquals(raw.GetProperty("Styles"), prompts.GetProperty("Styles"))
                || !JsonElement.DeepEquals(raw.GetProperty("Lines"), prompts.GetProperty("Lines")))
                throw new InvalidDataException("Raw manifest does not match the current approved prompts.");
            var styles = prompts.GetProperty("Styles").EnumerateArray().ToArray();
            var lines = prompts.GetProperty("Lines").EnumerateArray().ToArray();
            var events = Events.Load().ToDictionary(e => e.Id);
            if (dialogueR1)
            {
                using var baselineDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(Paths.Root, "expressive-prompts.json")));
                using var catalogDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(Paths.Root, "dialogue-catalog-r1.json")));
                var baseline = baselineDocument.RootElement;
                var catalog = catalogDocument.RootElement;
                if (prompts.GetProperty("DialogueRevision").GetString() != "dialogue-r1"
                    || catalog.GetProperty("DialogueRevision").GetString() != "dialogue-r1"
                    || catalog.GetProperty("GlobalStatus").GetString() != "draft_fixture_catalog"
                    || prompts.GetProperty("Persona").GetString() != baseline.GetProperty("Persona").GetString()
                    || !JsonElement.DeepEquals(prompts.GetProperty("Styles"), baseline.GetProperty("Styles")))
                    throw new InvalidDataException("Dialogue-r1 must retain the baseline persona/styles and draft catalog revision.");
                var entries = catalog.GetProperty("Entries").EnumerateArray().ToDictionary(e => e.GetProperty("EventId").GetString()!);
                if (entries.Count != events.Count || !entries.Keys.ToHashSet().SetEquals(events.Keys))
                    throw new InvalidDataException("Dialogue catalog must contain exactly the existing event IDs.");
                foreach (var (id, entry) in entries)
                {
                    var ev = events[id];
                    if (entry.GetProperty("Category").GetString() != ev.Category || entry.GetProperty("Priority").GetInt32() != ev.Priority
                        || !JsonElement.DeepEquals(entry.GetProperty("Facts"), JsonSerializer.SerializeToElement(ev.Facts)))
                        throw new InvalidDataException($"Dialogue catalog metadata differs for {id}.");
                }
                var baselineLines = baseline.GetProperty("Lines").EnumerateArray().ToDictionary(l => l.GetProperty("EventId").GetString()!);
                foreach (var line in lines)
                {
                    string id = line.GetProperty("EventId").GetString()!;
                    var original = baselineLines[id];
                    if (line.GetProperty("Seed").GetInt64() != original.GetProperty("Seed").GetInt64()
                        || !JsonElement.DeepEquals(line.GetProperty("Facts"), original.GetProperty("Facts"))
                        || line.GetProperty("Text").GetString() != entries[id].GetProperty("Text").GetString())
                        throw new InvalidDataException($"Dialogue-r1 line differs from its catalog or fixed seed/facts: {id}.");
                }
            }
            if (styles.Length != 3 || !styles.Select(s => s.GetProperty("Id").GetString()).ToHashSet().SetEquals(new[] { "neutral", "reassuring", "urgent" })
                || lines.Length != 3 || !lines.Select(l => l.GetProperty("EventId").GetString()).ToHashSet().SetEquals(EventIds))
                throw new InvalidDataException("Expected the three approved styles and three event lines.");
            foreach (var line in lines)
            {
                string eventId = line.GetProperty("EventId").GetString()!;
                if (!JsonElement.DeepEquals(line.GetProperty("Facts"), JsonSerializer.SerializeToElement(events[eventId].Facts)))
                    throw new InvalidDataException($"Structured event facts changed for {eventId}.");
            }
            var clips = raw.GetProperty("Clips").EnumerateArray().ToArray();
            var expected = styles.SelectMany(s => lines.Select(l => (Style: s.GetProperty("Id").GetString()!, Event: l.GetProperty("EventId").GetString()!))).ToHashSet();
            var seen = new HashSet<(string Style, string Event)>();
            var decoded = new Dictionary<(string Style, string Event), (float[] Samples, int Rate)>();
            foreach (var clip in clips)
            {
                var key = (Style: clip.GetProperty("StyleId").GetString()!, Event: clip.GetProperty("EventId").GetString()!);
                if (!expected.Contains(key) || !seen.Add(key)) throw new InvalidDataException("Unexpected or duplicate style/event pair in raw manifest.");
                var line = lines.Single(l => l.GetProperty("EventId").GetString() == key.Event);
                var style = styles.Single(s => s.GetProperty("Id").GetString() == key.Style);
                if (clip.GetProperty("Text").GetString() != line.GetProperty("Text").GetString()
                    || clip.GetProperty("Seed").GetInt64() != line.GetProperty("Seed").GetInt64()
                    || !JsonElement.DeepEquals(clip.GetProperty("Facts"), line.GetProperty("Facts"))
                    || clip.GetProperty("Prompt").GetString() != prompts.GetProperty("Persona").GetString() + " " + style.GetProperty("Prompt").GetString())
                    throw new InvalidDataException($"Raw clip text, facts, seed or prompt differs for {key}.");
                string relative = $"raw/{key.Style}/{key.Event}.wav";
                if (clip.GetProperty("RawPath").GetString() != relative) throw new InvalidDataException("Unexpected raw WAV path.");
                string path = Path.GetFullPath(Path.Combine(output, relative));
                if (!path.StartsWith(Path.GetFullPath(output) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Raw WAV path escaped the expressive output directory.");
                var bytes = File.ReadAllBytes(path);
                if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(clip.GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Raw WAV hash changed: {relative}");
                var (samples, rate) = Wav.Read(bytes);
                Validate(samples, rate);
                if (rate != clip.GetProperty("SampleRate").GetInt32() || samples.Length != clip.GetProperty("SampleCount").GetInt32())
                    throw new InvalidDataException($"Raw WAV sample metadata differs: {relative}");
                decoded.Add(key, (samples, rate));
            }
            if (clips.Length != 9 || !seen.SetEquals(expected)) throw new InvalidDataException("All nine unique style/event pairs are required.");
            var effects = new[]
            {
                new EffectSpec("clean", "Clean", 0, 0, 0, 1),
                new EffectSpec("pda_speaker", "PDA speaker", 180, 4800, 0.85, 1.35),
                new EffectSpec("radio", "Radio", 300, 3400, 1, 1.8),
            };
            // Require the existing Emma reference; never create or alter baseline audio here.
            var references = effects.Select(e => new EmmaReference(e.Id, e.Label, $"../emma-tuning/conversational/reel_{e.Id}.wav")).ToList();
            foreach (var reference in references)
                if (!File.Exists(Path.Combine(output, reference.Path))) throw new FileNotFoundException("Missing Emma conversational baseline reel.", reference.Path);
            if (dialogueR1)
            {
                if (!File.Exists(Path.Combine(output, "../expressive/index.html"))) throw new FileNotFoundException("Missing previous-wording comparison page.");
                foreach (var style in styles)
                    foreach (var effect in effects)
                    {
                        string previous = $"../expressive/processed/{style.GetProperty("Id").GetString()}/reel_{effect.Id}.wav";
                        if (!File.Exists(Path.Combine(output, previous))) throw new FileNotFoundException("Missing previous-wording style reel.", previous);
                    }
            }
            foreach (var file in new[] { "manifest.json", "index.html" }) File.Delete(Path.Combine(output, file));
            var results = new List<ExpressiveStyle>();
            foreach (var style in styles)
            {
                string styleId = style.GetProperty("Id").GetString()!;
                var rendered = new List<ExpressiveLine>();
                var parts = effects.ToDictionary(e => e.Id, _ => new List<float[]>());
                int rate = 0;
                foreach (var line in lines)
                {
                    string eventId = line.GetProperty("EventId").GetString()!;
                    var (samples, sampleRate) = decoded[(styleId, eventId)];
                    if (rate != 0 && rate != sampleRate) throw new InvalidDataException("Sample rate changed within a style reel.");
                    rate = sampleRate;
                    var processed = new List<EffectClip>();
                    foreach (var effect in effects)
                    {
                        var timer = Stopwatch.StartNew();
                        var buffer = effect.Id == "clean" ? samples
                            : RadioDsp.ProcessProfile(samples, rate, effect.LowHz, effect.HighHz, effect.Wet, effect.Drive);
                        double dspSeconds = timer.Elapsed.TotalSeconds;
                        var (normalized, gain) = Normalize(buffer, rate);
                        var clip = WriteClip(output, $"processed/{styleId}/{eventId}_{effect.Id}.wav", normalized, rate, gain);
                        processed.Add(new EffectClip(effect.Id, dspSeconds, clip));
                        parts[effect.Id].Add(normalized);
                    }
                    rendered.Add(new ExpressiveLine(eventId, line.GetProperty("Text").GetString()!, $"raw/{styleId}/{eventId}.wav", Measure(samples, rate), processed));
                }
                var reels = effects.Select(e => new EffectReel(e.Id, WriteReel(output, $"processed/{styleId}/reel_{e.Id}.wav", parts[e.Id], rate))).ToList();
                results.Add(new ExpressiveStyle(styleId, style.GetProperty("Label").GetString()!, rendered, reels));
            }
            var generated = DateTime.UtcNow;
            var manifest = new
            {
                GeneratedUtc = generated, Raw = raw,
                Processing = new
                {
                    Effects = effects, ReelGapSeconds = GapSeconds, TargetGatedRmsDbfs = TargetDb,
                    PeakCapDbfs = PeakCapDb, GateDbfs = GateDb, GateWindowMs = 20,
                    Dsp = "Same RadioDsp.ProcessProfile as Emma: two second-order sections per edge, Q 0.7071; wet * tanh(filtered * drive) + (1 - wet) * dry; no pre-saturation boost, cues, noise, pitch shift or spatial effects.",
                    Normalization = "One fixed gain per effect per speech clip; peak-limited approximate gated RMS match. No additional reel gain. Levels measured before PCM16 quantization, not perceptual loudness matching.",
                    Timing = "DSP time excludes normalization, measurements and writes; clean time measures waveform selection only. Standalone measurements, not in-game performance acceptance.",
                },
                EmmaReference = references, Styles = results,
            };
            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            if (dialogueR1)
            {
                var revisedManifest = JsonSerializer.SerializeToElement(manifest).EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value);
                revisedManifest["DialogueRevision"] = "dialogue-r1";
                revisedManifest["ConfigFile"] = configFile;
                revisedManifest["PreviousWording"] = new
                {
                    Page = "../expressive/index.html",
                    Reels = results.SelectMany(s => effects.Select(e => new { StyleId = s.StyleId, EffectId = e.Id, Path = $"../expressive/processed/{s.StyleId}/reel_{e.Id}.wav" })),
                    EmmaReferenceNote = "Emma reference uses the earlier wording.",
                };
                json = JsonSerializer.Serialize(revisedManifest, new JsonSerializerOptions { WriteIndented = true });
            }
            string html = ExpressiveHtml(results, effects, references, generated, dialogueR1);
            File.WriteAllText(Path.Combine(output, "manifest.json"), json);
            File.WriteAllText(Path.Combine(output, "index.html"), html);
            Console.WriteLine($"Expressive report complete: {Path.Combine(output, "index.html")}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Expressive report failed: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    static string ExpressiveHtml(List<ExpressiveStyle> styles, EffectSpec[] effects, List<EmmaReference> references, DateTime generated, bool dialogueR1 = false)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        static string Player(string path, string label) => $"<audio controls preload=\"none\" aria-label=\"{E(label)}\" src=\"{E(path)}\"></audio>";
        var html = new StringBuilder("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Expressive PDA voice audition</title>
            <style>body{font:16px system-ui,sans-serif;max-width:1100px;margin:2rem auto;padding:0 1rem;color:#e6edf3;background:#111923}h1,h2,h3{color:#aaddf4}section{border-top:1px solid #435364;margin-top:2rem}audio{width:100%}a{color:#aaddf4}.players{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:1rem}details{margin:1rem 0}summary{cursor:pointer}</style>
            </head><body><h1>Expressive PDA voice audition</h1>
            <p>Compare a British female voice concept with Neutral, Reassuring and Urgent direction. The words stay the same. Listen for conversational warmth, clear alerts and composed delivery.</p>
            <p>This is a new voice concept beside Emma, not an exact clone. Repeating the same persona and seed does not lock the speaker's identity, so vocal character can vary between clips.</p>
            <p>Start with Clean to judge the voice, then compare PDA speaker and Radio. Levels are approximately matched; perceived loudness can differ. Keep playback volume consistent. Starting a clip pauses the others.</p>
            """);
        html.AppendLine($"<p>Generated {generated:yyyy-MM-dd} · <a href=\"manifest.json\">Measurements and settings (JSON)</a></p>");
        if (dialogueR1)
        {
            html.Replace("<title>Expressive PDA voice audition</title>", "<title>PDA dialogue r1 audition</title>");
            html.Replace("<h1>Expressive PDA voice audition</h1>", "<h1>PDA dialogue r1 audition</h1>");
            html.Replace("The words stay the same.", "The revised words stay the same across these three delivery styles.");
            html.AppendLine("<p>Dialogue r1 aims for a practical, observant suit assistant with restrained warmth and support. <a href=\"../expressive/index.html\">Compare the previous wording</a>, or expand a style's previous-wording reels below.</p>");
        }
        html.AppendLine(dialogueR1
            ? "<section><h2>Emma reference — earlier wording</h2><p>The earlier British female voice and script, for reference.</p><div class=\"players\">"
            : "<section><h2>Emma reference — conversational wording</h2><p>The existing British female voice, reading the same words.</p><div class=\"players\">");
        foreach (var reference in references)
            html.AppendLine($"<div><h3>{E(reference.Label)}</h3>{Player(reference.Path, "Emma reference " + reference.Label)}<p><a href=\"{E(reference.Path)}\">Open {E(reference.Label)} reel</a></p></div>");
        html.AppendLine("</div></section>");
        foreach (var style in styles)
        {
            html.AppendLine($"<section><h2>{E(style.Label)}</h2><div class=\"players\">");
            foreach (var effect in effects)
                html.AppendLine($"<div><h3>{E(effect.Label)}</h3>{Player(style.Reels.Single(r => r.EffectId == effect.Id).Reel.Path, style.Label + " " + effect.Label + " reel")}</div>");
            if (dialogueR1)
            {
                html.AppendLine("</div>");
                html.AppendLine("<details><summary>Previous wording — same delivery style and effects</summary><div class=\"players\">");
                foreach (var effect in effects)
                    html.AppendLine($"<div><h3>{E(effect.Label)}</h3>{Player($"../expressive/processed/{style.StyleId}/reel_{effect.Id}.wav", style.Label + " previous wording " + effect.Label)}</div>");
                html.AppendLine("</div></details><details><summary>Individual lines and transcripts</summary>");
            }
            else html.AppendLine("</div><details><summary>Individual lines and transcripts</summary>");
            foreach (var line in style.Lines)
            {
                html.AppendLine($"<p>{E(line.Text)}</p><div class=\"players\">");
                foreach (var effect in effects)
                    html.AppendLine($"<div><p>{E(effect.Label)}</p>{Player(line.Effects.Single(c => c.EffectId == effect.Id).Clip.Path, style.Label + " " + line.EventId + " " + effect.Label)}</div>");
                html.AppendLine("</div>");
            }
            html.AppendLine("</details></section>");
        }
        html.AppendLine("<script>document.addEventListener('play',event=>{if(event.target instanceof HTMLAudioElement){document.querySelectorAll('audio').forEach(audio=>{if(audio!==event.target)audio.pause();});}},true);</script></body></html>");
        return html.ToString();
    }
}
