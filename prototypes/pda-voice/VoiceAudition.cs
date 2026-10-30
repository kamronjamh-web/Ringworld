using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using KokoroSharp;
using KokoroSharp.Core;
using Microsoft.ML.OnnxRuntime;

namespace PdaVoicePrototype;

public static partial class VoiceAudition
{
    const int Threads = 4;
    const double TargetDb = -20, PeakCapDb = -1, GateDb = -45, GapSeconds = 0.7;
    static readonly (string Id, string Name, string Accent)[] Voices =
    {
        ("af_sarah", "Sarah", "American"), ("bf_emma", "Emma", "British"),
        ("af_kore", "Kore", "American"), ("af_heart", "Heart", "American"),
    };
    static readonly string[] EventIds = { "storm_incoming", "emitter_discovered", "low_oxygen" };

    sealed record Levels(double RmsDbfs, double GatedRmsDbfs, double PeakDbfs);
    sealed record Clip(string Path, double Seconds, double GainDb, Levels Levels);
    sealed record Line(string EventId, string Text, double SynthSeconds, Levels SourceLevels, Clip Clean, Clip Light);
    sealed record Reel(string Path, double Seconds, Levels Levels);
    sealed record VoiceResult(string VoiceId, string Name, string Accent, List<Line> Lines, Reel CleanReel, Reel LightReel);

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length != 0) throw new ArgumentException("audition takes no arguments; output is reused under output/audition/.");
            var model = Path.Combine(Paths.Models, "kokoro.onnx");
            if (!File.Exists(model)) throw new FileNotFoundException("Local Kokoro model is missing; audition does not download models.", model);
            var events = Events.Load().ToDictionary(e => e.Id);
            var lines = EventIds.Select(id => events.TryGetValue(id, out var ev) && !string.IsNullOrWhiteSpace(ev.Fallback)
                ? ev : throw new InvalidDataException($"Missing authored event: {id}")).ToArray();
            // Resolve every packaged voice before spending time on inference. Never substitute a voice.
            foreach (var spec in Voices)
            {
                var voicePath = Path.Combine(AppContext.BaseDirectory, "voices", spec.Id + ".npy");
                if (!File.Exists(voicePath)) throw new FileNotFoundException($"Missing packaged Kokoro voice: {spec.Id}", voicePath);
            }
            var voices = Voices.Select(v => KokoroVoiceManager.GetVoice(v.Id)
                ?? throw new InvalidDataException($"Missing Kokoro voice: {v.Id}")).ToArray();
            var output = Path.Combine(Paths.Output, "audition");
            Directory.CreateDirectory(output);
            // An interrupted rerun must not leave an old report claiming the new audio is complete.
            foreach (var file in new[] { "index.html", "manifest.json" })
                File.Delete(Path.Combine(output, file));
            using var opts = new SessionOptions { IntraOpNumThreads = Threads, InterOpNumThreads = 1 };
            var load = Stopwatch.StartNew();
            using var synth = KokoroWavSynthesizer.LoadModel(model, opts);
            double loadSeconds = load.Elapsed.TotalSeconds;
            var results = new List<VoiceResult>();
            for (int v = 0; v < Voices.Length; v++)
            {
                var spec = Voices[v];
                var rendered = new List<Line>();
                var cleanParts = new List<float[]>();
                var lightParts = new List<float[]>();
                int rate = 0;
                foreach (var ev in lines)
                {
                    var timer = Stopwatch.StartNew();
                    var bytes = synth.Synthesize(ev.Fallback, voices[v]); // Default normal speed; no pitch or delivery tricks.
                    double seconds = timer.Elapsed.TotalSeconds;
                    var (samples, sampleRate) = Decode(bytes);
                    Validate(samples, sampleRate);
                    if (rate != 0 && rate != sampleRate) throw new InvalidDataException("Kokoro sample rate changed within a reel.");
                    rate = sampleRate;
                    var source = Measure(samples, rate);
                    var (clean, cleanGain) = Normalize(samples, rate);
                    var (light, lightGain) = Normalize(RadioDsp.ProcessLight(samples, rate), rate);
                    var cleanClip = WriteClip(output, $"{spec.Id}/{ev.Id}_clean.wav", clean, rate, cleanGain);
                    var lightClip = WriteClip(output, $"{spec.Id}/{ev.Id}_light.wav", light, rate, lightGain);
                    rendered.Add(new Line(ev.Id, ev.Fallback, seconds, source, cleanClip, lightClip));
                    cleanParts.Add(clean);
                    lightParts.Add(light);
                    Console.WriteLine($"[{spec.Id}] {ev.Id}: {samples.Length / (double)rate:F2}s audio in {seconds:F2}s; clean/light gated RMS {cleanClip.Levels.GatedRmsDbfs:F1}/{lightClip.Levels.GatedRmsDbfs:F1} dBFS");
                }
                var cleanReel = WriteReel(output, $"{spec.Id}/reel_clean.wav", cleanParts, rate);
                var lightReel = WriteReel(output, $"{spec.Id}/reel_light.wav", lightParts, rate);
                results.Add(new VoiceResult(spec.Id, spec.Name, spec.Accent, rendered, cleanReel, lightReel));
            }
            var generated = DateTime.UtcNow;
            var manifest = new
            {
                GeneratedUtc = generated, Model = "models/kokoro.onnx", Engine = "KokoroSharp.CPU 0.8.4",
                Settings = new
                {
                    IntraOpThreads = Threads, InterOpThreads = 1, ExecutionProvider = "CPU", Speed = 1.0,
                    TextSource = "events.json authored fallback", EventOrder = EventIds, ReelGapSeconds = GapSeconds,
                    TargetGatedRmsDbfs = TargetDb, PeakCapDbfs = PeakCapDb, GateDbfs = GateDb, GateWindowMs = 20,
                    Normalization = "One constant gain per variant per speech clip; target gated RMS limited by peak cap. No compressor. Reels concatenate normalized clips without further gain.",
                    LightDsp = "75% dry + 25% second-order 120 Hz high-pass then second-order 7500 Hz low-pass, Q 0.7071; no saturation, cues or spatial effects.",
                    Metrics = "Pre-PCM16-quantization samples; RMS over all samples, gated RMS over 20 ms blocks above -45 dBFS. Approximate level matching, not perceptual loudness matching.",
                    Timing = "No warm-up; first synthesis includes first-inference overhead. Not a benchmark.",
                },
                LoadSeconds = loadSeconds, Voices = results,
            };
            File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "index.html"), Html(results, generated));
            Console.WriteLine($"Audition complete: {Path.Combine(output, "index.html")}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Audition failed: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    sealed record EffectSpec(string Id, string Label, double LowHz, double HighHz, double Wet, double Drive);
    sealed record EffectClip(string EffectId, double DspSeconds, Clip Clip);
    sealed record EmmaLine(string EventId, string OriginalText, string Text, Dictionary<string, JsonElement> Facts,
        double SynthSeconds, Levels SourceLevels, List<EffectClip> Effects);
    sealed record EffectReel(string EffectId, Reel Reel);
    sealed record ScriptResult(string ScriptId, string Label, List<EmmaLine> Lines, List<EffectReel> Reels);

    public static int RunEmma(string[] args)
    {
        try
        {
            if (args.Length != 0) throw new ArgumentException("audition-emma takes no arguments; output is reused under output/emma-tuning/.");
            var model = Path.Combine(Paths.Models, "kokoro.onnx");
            if (!File.Exists(model)) throw new FileNotFoundException("Local Kokoro model is missing; audition does not download models.", model);
            var voicePath = Path.Combine(AppContext.BaseDirectory, "voices", "bf_emma.npy");
            if (!File.Exists(voicePath)) throw new FileNotFoundException("Missing packaged Kokoro voice: bf_emma", voicePath);
            var voice = KokoroVoiceManager.GetVoice("bf_emma") ?? throw new InvalidDataException("Missing Kokoro voice: bf_emma");
            var events = Events.Load().ToDictionary(e => e.Id);
            var lines = EventIds.Select(id => events.TryGetValue(id, out var ev) && !string.IsNullOrWhiteSpace(ev.Fallback)
                ? ev : throw new InvalidDataException($"Missing authored event: {id}")).ToArray();
            var conversational = new Dictionary<string, string>
            {
                ["storm_incoming"] = "A storm front is on the way. It'll be here in 4 minutes.",
                ["emitter_discovered"] = "There's an emitter structure 1.2 kilometers north of you. Its origin is unknown.",
                ["low_oxygen"] = "Your oxygen is at 15 percent. Let's get you back to breathable air.",
            };
            var effects = new[]
            {
                new EffectSpec("clean", "Clean", 0, 0, 0, 1),
                new EffectSpec("pda_speaker", "PDA speaker", 180, 4800, 0.85, 1.35),
                new EffectSpec("radio", "Radio", 300, 3400, 1, 1.8),
            };
            var output = Path.Combine(Paths.Output, "emma-tuning");
            Directory.CreateDirectory(output);
            foreach (var file in new[] { "index.html", "manifest.json" })
                File.Delete(Path.Combine(output, file));
            using var opts = new SessionOptions { IntraOpNumThreads = Threads, InterOpNumThreads = 1 };
            var load = Stopwatch.StartNew();
            using var synth = KokoroWavSynthesizer.LoadModel(model, opts);
            double loadSeconds = load.Elapsed.TotalSeconds;
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            double peakAfterLoadMb = process.PeakWorkingSet64 / 1048576.0;
            var results = new List<ScriptResult>();
            foreach (var (scriptId, label) in new[] { ("original", "Original"), ("conversational", "Conversational") })
            {
                var rendered = new List<EmmaLine>();
                var parts = effects.ToDictionary(e => e.Id, _ => new List<float[]>());
                int rate = 0;
                foreach (var ev in lines)
                {
                    string text = scriptId == "original" ? ev.Fallback : conversational[ev.Id];
                    var timer = Stopwatch.StartNew();
                    var bytes = synth.Synthesize(text, voice); // Normal speed and default segmentation/pauses, as in the original audition.
                    double synthSeconds = timer.Elapsed.TotalSeconds;
                    var (samples, sampleRate) = Decode(bytes);
                    Validate(samples, sampleRate);
                    if (rate != 0 && rate != sampleRate) throw new InvalidDataException("Kokoro sample rate changed within a reel.");
                    rate = sampleRate;
                    var clips = new List<EffectClip>();
                    foreach (var effect in effects)
                    {
                        timer.Restart();
                        var processed = effect.Id == "clean" ? samples
                            : RadioDsp.ProcessProfile(samples, rate, effect.LowHz, effect.HighHz, effect.Wet, effect.Drive);
                        double dspSeconds = timer.Elapsed.TotalSeconds;
                        var (normalized, gain) = Normalize(processed, rate);
                        var clip = WriteClip(output, $"{scriptId}/{ev.Id}_{effect.Id}.wav", normalized, rate, gain);
                        clips.Add(new EffectClip(effect.Id, dspSeconds, clip));
                        parts[effect.Id].Add(normalized);
                    }
                    rendered.Add(new EmmaLine(ev.Id, ev.Fallback, text, ev.Facts, synthSeconds, Measure(samples, rate), clips));
                    Console.WriteLine($"[Emma/{label}] {ev.Id}: {samples.Length / (double)rate:F2}s audio in {synthSeconds:F2}s; three effects from one synthesis");
                }
                var reels = effects.Select(e => new EffectReel(e.Id, WriteReel(output, $"{scriptId}/reel_{e.Id}.wav", parts[e.Id], rate))).ToList();
                results.Add(new ScriptResult(scriptId, label, rendered, reels));
            }
            process.Refresh();
            double peakAfterRunMb = process.PeakWorkingSet64 / 1048576.0;
            var generated = DateTime.UtcNow;
            var manifest = new
            {
                GeneratedUtc = generated, VoiceId = "bf_emma", VoiceName = "Emma", Accent = "British",
                Model = "models/kokoro.onnx", Engine = "KokoroSharp.CPU 0.8.4",
                Settings = new
                {
                    IntraOpThreads = Threads, InterOpThreads = 1, ExecutionProvider = "CPU", Speed = 1.0,
                    SegmentationAndPauses = "Package defaults unchanged; same synthesis call as original audition.",
                    Scripts = "Original: events.json fallback unchanged. Conversational: hand-authored warmer wording; no LLM or emotion control. Original text and structured facts are included per line for inspection.",
                    EventOrder = EventIds, ReelGapSeconds = GapSeconds, Effects = effects,
                    Dsp = "Non-clean effects use two cascaded second-order biquads per edge, Q 0.7071, then wet * tanh(filteredSample * drive) + (1 - wet) * originalSample. No pre-saturation peak boost, cues, noise, spatial effects or pitch shift.",
                    TargetGatedRmsDbfs = TargetDb, PeakCapDbfs = PeakCapDb, GateDbfs = GateDb, GateWindowMs = 20,
                    Normalization = "One constant gain per effect per speech clip. Reels concatenate normalized clips without additional gain. Approximate level matching, not perceptual loudness matching.",
                    Metrics = "RMS/peak measured before PCM16 quantization. Gated RMS uses 20 ms blocks above -45 dBFS.",
                    Timing = "No warm-up. First synthesis includes first-inference overhead. DSP time covers effect processing only, excluding normalization, measurements and file writes. Timings and process peaks are descriptive; not in-game frame cost or performance approval.",
                },
                LoadSeconds = loadSeconds, PeakWorkingSetAfterLoadMb = peakAfterLoadMb, PeakWorkingSetAfterRunMb = peakAfterRunMb,
                Scripts = results,
            };
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            var html = EmmaHtml(results, effects, generated);
            File.WriteAllText(Path.Combine(output, "manifest.json"), json);
            File.WriteAllText(Path.Combine(output, "index.html"), html);
            Console.WriteLine($"Emma audition complete: {Path.Combine(output, "index.html")}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Emma audition failed: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    static string EmmaHtml(List<ScriptResult> scripts, EffectSpec[] effects, DateTime generated)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        static string Player(string path, string label) => $"<audio controls preload=\"none\" aria-label=\"{E(label)}\" src=\"{E(path)}\"></audio>";
        var html = new StringBuilder("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Emma PDA tuning</title>
            <style>body{font:16px system-ui,sans-serif;max-width:1100px;margin:2rem auto;padding:0 1rem;color:#e6edf3;background:#111923}h1,h2,h3{color:#aaddf4}section{border-top:1px solid #435364;margin-top:2rem}audio{width:100%}a{color:#aaddf4}.players{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:1rem}details{margin:1rem 0}summary{cursor:pointer}</style>
            </head><body><h1>Emma — PDA tuning</h1>
            <p>Compare the same British female voice with the original script and warmer, more conversational wording. The conversational version changes the words, not an emotion toggle. Both use Emma's normal speech.</p>
            <p>Start with Clean to judge the delivery, then try PDA speaker and Radio to choose the device character. Listen for warmth, clarity and a tone you want to hear during play.</p>
            <p>Levels are approximately matched; perceived loudness can differ. Keep playback volume consistent. Starting a clip pauses the others. Reels pause briefly between the warning, discovery and oxygen alert.</p>
            """);
        html.AppendLine($"<p>Generated {generated:yyyy-MM-dd} · <a href=\"manifest.json\">Measurements and settings (JSON)</a></p>");
        foreach (var script in scripts)
        {
            html.AppendLine($"<section><h2>{E(script.Label)}</h2><div class=\"players\">");
            foreach (var effect in effects)
            {
                var reel = script.Reels.Single(r => r.EffectId == effect.Id).Reel;
                html.AppendLine($"<div><h3>{E(effect.Label)}</h3>{Player(reel.Path, script.Label + " " + effect.Label + " reel")}</div>");
            }
            html.AppendLine("</div><details><summary>Individual lines and transcripts</summary>");
            foreach (var line in script.Lines)
            {
                html.AppendLine($"<p>{E(line.Text)}</p><div class=\"players\">");
                foreach (var effect in effects)
                {
                    var clip = line.Effects.Single(c => c.EffectId == effect.Id).Clip;
                    html.AppendLine($"<div><p>{E(effect.Label)}</p>{Player(clip.Path, script.Label + " " + line.EventId + " " + effect.Label)}</div>");
                }
                html.AppendLine("</div>");
            }
            html.AppendLine("</details></section>");
        }
        html.AppendLine("<script>document.addEventListener('play',event=>{if(event.target instanceof HTMLAudioElement){document.querySelectorAll('audio').forEach(audio=>{if(audio!==event.target)audio.pause();});}},true);</script></body></html>");
        return html.ToString();
    }

    static (float[] Samples, int Rate) Decode(byte[] bytes)
    {
        if (bytes.Length > 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F') return Wav.Read(bytes);
        if (bytes.Length % 2 != 0) throw new InvalidDataException("Odd-length Kokoro PCM16 buffer.");
        var samples = new float[bytes.Length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        return (samples, 24000);
    }

    static void Validate(float[] samples, int rate)
    {
        if (rate <= 15000 || samples.Length == 0 || samples.Any(s => !float.IsFinite(s)))
            throw new InvalidDataException("Invalid, empty or nonfinite speech buffer (sample rate must exceed 15 kHz).");
        if (samples.Max(s => Math.Abs(s)) <= 1e-6f) throw new InvalidDataException("Silent speech buffer.");
    }

    static double Db(double amplitude) => 20 * Math.Log10(Math.Max(amplitude, 1e-12));

    static Levels Measure(float[] samples, int rate)
    {
        Validate(samples, rate);
        double sum = 0, gatedSum = 0, peak = 0;
        int gatedCount = 0, window = Math.Max(1, rate / 50);
        double gatePower = Math.Pow(10, GateDb / 10);
        for (int offset = 0; offset < samples.Length; offset += window)
        {
            int count = Math.Min(window, samples.Length - offset);
            double block = 0;
            for (int i = offset; i < offset + count; i++)
            {
                double s = samples[i];
                block += s * s;
                peak = Math.Max(peak, Math.Abs(s));
            }
            sum += block;
            if (block / count > gatePower) { gatedSum += block; gatedCount += count; }
        }
        if (gatedCount == 0) throw new InvalidDataException("Speech buffer contains no blocks above the -45 dBFS gate.");
        return new Levels(Db(Math.Sqrt(sum / samples.Length)), Db(Math.Sqrt(gatedSum / gatedCount)), Db(peak));
    }

    static (float[] Samples, double GainDb) Normalize(float[] input, int rate)
    {
        var levels = Measure(input, rate);
        double gainDb = Math.Min(TargetDb - levels.GatedRmsDbfs, PeakCapDb - levels.PeakDbfs);
        double gain = Math.Pow(10, gainDb / 20);
        return (input.Select(s => (float)(s * gain)).ToArray(), gainDb);
    }

    static Clip WriteClip(string output, string relative, float[] samples, int rate, double gainDb)
    {
        var levels = Measure(samples, rate);
        Wav.Write16(Path.Combine(output, relative), samples, rate);
        return new Clip(relative, samples.Length / (double)rate, gainDb, levels);
    }

    static Reel WriteReel(string output, string relative, List<float[]> parts, int rate)
    {
        int gap = (int)(GapSeconds * rate);
        var samples = new float[parts.Sum(p => p.Length) + gap * (parts.Count - 1)];
        int offset = 0;
        foreach (var part in parts)
        {
            Array.Copy(part, 0, samples, offset, part.Length);
            offset += part.Length + gap;
        }
        var clip = WriteClip(output, relative, samples, rate, 0);
        return new Reel(clip.Path, clip.Seconds, clip.Levels);
    }

    static string Html(List<VoiceResult> voices, DateTime generated)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        static string Player(string path, string label) => $"<audio controls preload=\"none\" aria-label=\"{E(label)}\" src=\"{E(path)}\"></audio>";
        var html = new StringBuilder("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>PDA voice audition</title>
            <style>body{font:16px system-ui,sans-serif;max-width:1100px;margin:2rem auto;padding:0 1rem;color:#e6edf3;background:#111923}h1,h2,h3{color:#aaddf4}section{border-top:1px solid #435364;margin-top:2rem}table{width:100%;border-collapse:collapse}th,td{text-align:left;vertical-align:top;padding:.6rem;border-bottom:1px solid #435364}audio{width:100%;min-width:200px}code{color:#c4e6f4}a{color:#aaddf4}.players{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:1rem}.scroll{overflow-x:auto}</style>
            </head><body><h1>PDA voice audition</h1>
            <p>Listen for a cool, clinical female voice with clear delivery, without an intimate whisper. Compare how each candidate handles the warning, discovery and oxygen alert.</p>
            <p>Every voice reads the same three authored lines at normal speed. Start with a clean reel to judge delivery, then compare the light PDA version for a gentle device tone. Filtering preserves each voice's inherent breathiness.</p>
            <p>Levels are approximately matched; perceived loudness may still differ. Keep your playback volume consistent. Starting a clip pauses any other clip playing on this page. Reels pause briefly between lines.</p>
            """);
        html.AppendLine($"<p>Generated {generated:yyyy-MM-dd} · <a href=\"manifest.json\">Measurements and settings (JSON)</a></p>");
        foreach (var voice in voices)
        {
            html.AppendLine($"<section><h2>{E(voice.Name)} — {E(voice.Accent)} female</h2><h3>Three-line comparison reel</h3><div class=\"players\">");
            html.AppendLine($"<div><p>A · Clean · {Fmt.F(voice.CleanReel.Seconds, 2)} s</p>{Player(voice.CleanReel.Path, voice.Name + " clean reel")}</div>");
            html.AppendLine($"<div><p>B · Light PDA · {Fmt.F(voice.LightReel.Seconds, 2)} s</p>{Player(voice.LightReel.Path, voice.Name + " light PDA reel")}</div></div>");
            html.AppendLine("<details><summary>Individual lines and transcripts</summary><div class=\"scroll\"><table><thead><tr><th scope=\"col\">Authored transcript</th><th scope=\"col\">A · Clean</th><th scope=\"col\">B · Light PDA</th></tr></thead><tbody>");
            foreach (var line in voice.Lines)
            {
                html.AppendLine($"<tr><td><p>{E(line.Text)}</p></td>");
                foreach (var clip in new[] { line.Clean, line.Light })
                    html.AppendLine($"<td>{Player(clip.Path, voice.Name + " " + line.EventId + " " + (clip == line.Clean ? "clean" : "light PDA"))}<p><small>{Fmt.F(clip.Seconds, 2)} s</small></p></td>");
                html.AppendLine("</tr>");
            }
            html.AppendLine("</tbody></table></div></details></section>");
        }
        html.AppendLine("<script>document.addEventListener('play',event=>{if(event.target instanceof HTMLAudioElement){document.querySelectorAll('audio').forEach(audio=>{if(audio!==event.target)audio.pause();});}},true);</script></body></html>");
        return html.ToString();
    }
}
