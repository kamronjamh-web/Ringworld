using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KokoroSharp;
using KokoroSharp.Core;
using Microsoft.ML.OnnxRuntime;

namespace PdaVoicePrototype;

public sealed class PickedLine
{
    public string EventId { get; set; } = "";
    public string Source { get; set; } = "";   // model that produced the line
    public string Text { get; set; } = "";
}

public sealed record VoiceSpec(string Engine, string Voice, string Label);

public sealed class TtsLineResult
{
    public string Voice { get; set; } = "";
    public string EventId { get; set; } = "";
    public string Variant { get; set; } = "";  // authored | llm
    public string Text { get; set; } = "";
    public string Wav { get; set; } = "";
    public string? RadioWav { get; set; }
    public double AudioSeconds { get; set; }
    public double SynthSeconds { get; set; }
    public double Rtf => AudioSeconds > 0 ? SynthSeconds / AudioSeconds : 0;
}

public sealed class TtsWorkerResult
{
    public string Engine { get; set; } = "";
    public string? Error { get; set; }
    public double LoadSeconds { get; set; }
    public double WorkingSetAfterLoadMb { get; set; }
    public double PeakWorkingSetMb { get; set; }
    public string Note { get; set; } = "";
    public List<TtsLineResult> Lines { get; set; } = new();
}

public static class TtsBench
{
    public static readonly VoiceSpec[] Voices =
    {
        new("kokoro", "af_nicole", "kokoro-af_nicole"),
        new("kokoro", "bm_george", "kokoro-bm_george"),
        new("piper", "en_US-lessac-medium", "piper-lessac"),
    };

    const int KokoroThreads = 4; // the game shares the CPU

    static List<(string EventId, string Variant, string Text)> BuildLines()
    {
        var events = Events.Load().ToDictionary(e => e.Id);
        var picksPath = Path.Combine(Paths.Root, "tts_picks.json");
        var picks = JsonSerializer.Deserialize<List<PickedLine>>(File.ReadAllText(picksPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var lines = new List<(string, string, string)>();
        foreach (var p in picks)
        {
            lines.Add((p.EventId, "authored", events[p.EventId].Fallback));
            lines.Add((p.EventId, "llm", p.Text));
        }
        return lines;
    }

    // ---------------- worker (one engine per process for clean RAM numbers) ----------------
    public static Task<int> RunWorker(string engine, string outPath)
    {
        var res = new TtsWorkerResult { Engine = engine };
        try
        {
            var lines = BuildLines();
            if (engine == "kokoro") RunKokoro(res, lines);
            else if (engine == "piper") RunPiper(res, lines);
            else throw new ArgumentException("unknown engine " + engine);
            res.PeakWorkingSetMb = Math.Max(res.PeakWorkingSetMb, Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0);
        }
        catch (Exception ex)
        {
            res.Error = ex.GetType().Name + ": " + ex.Message;
            Console.WriteLine($"  [{engine}] FAILED: {ex}");
        }
        File.WriteAllText(outPath, JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = true }));
        return Task.FromResult(res.Error == null ? 0 : 2);
    }

    static string WavPath(string label, string eventId, string variant) =>
        Path.Combine(Paths.Output, "tts", label, $"{eventId}_{variant}.wav");

    static void RunKokoro(TtsWorkerResult res, List<(string EventId, string Variant, string Text)> lines)
    {
        var modelPath = Path.Combine(Paths.Models, "kokoro.onnx");
        var sw = Stopwatch.StartNew();
        var opts = new SessionOptions { IntraOpNumThreads = KokoroThreads, InterOpNumThreads = 1 }; // CPU execution provider only
        using var synth = KokoroWavSynthesizer.LoadModel(modelPath, opts);
        res.LoadSeconds = sw.Elapsed.TotalSeconds;
        res.WorkingSetAfterLoadMb = Process.GetCurrentProcess().WorkingSet64 / 1048576.0;
        res.Note = $"KokoroSharp.CPU 0.8.4, Kokoro-82M v1.0 fp32 ONNX, ONNX Runtime CPU, {KokoroThreads} intra-op threads, MisakiSharp managed G2P (no espeak).";

        // Warm-up (first inference allocates), not recorded.
        synth.Synthesize("Systems nominal.", KokoroVoiceManager.GetVoice("af_nicole"));

        foreach (var spec in Voices.Where(v => v.Engine == "kokoro"))
        {
            var voice = KokoroVoiceManager.GetVoice(spec.Voice);
            foreach (var (eventId, variant, text) in lines)
            {
                var t = Stopwatch.StartNew();
                var bytes = synth.Synthesize(text, voice);
                var secs = t.Elapsed.TotalSeconds;
                var path = WavPath(spec.Label, eventId, variant);
                var (samples, rate) = DecodeKokoro(bytes);
                Wav.Write16(path, samples, rate);
                res.Lines.Add(new TtsLineResult { Voice = spec.Label, EventId = eventId, Variant = variant, Text = text, Wav = path, AudioSeconds = samples.Length / (double)rate, SynthSeconds = secs });
                Console.WriteLine($"  [{spec.Label}] {eventId}/{variant} {samples.Length / (double)rate:F2}s audio in {secs:F2}s");
            }
        }
    }

    // KokoroSharp.Synthesize returns headerless 16-bit mono PCM at 24 kHz (verified by byte count vs. duration).
    static (float[] Samples, int Rate) DecodeKokoro(byte[] bytes)
    {
        if (bytes.Length > 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F') return Wav.Read(bytes);
        var f = new float[bytes.Length / 2];
        for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        return (f, 24000);
    }

    static void RunPiper(TtsWorkerResult res, List<(string EventId, string Variant, string Text)> lines)
    {
        var exe = Path.Combine(Paths.Models, "piper-bin", "piper", "piper.exe");
        var model = Path.Combine(Paths.Models, "piper", "en_US-lessac-medium.onnx");
        var spec = Voices.First(v => v.Engine == "piper");
        double peak = 0;

        (double Seconds, double PeakMb) Run(string text, string wav)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
            var psi = new ProcessStartInfo(exe, $"-m \"{model}\" -f \"{wav}\" -q")
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
            };
            var sw = Stopwatch.StartNew();
            using var p = Process.Start(psi)!;
            p.ErrorDataReceived += (_, _) => { }; p.OutputDataReceived += (_, _) => { };
            p.BeginErrorReadLine(); p.BeginOutputReadLine();
            p.StandardInput.WriteLine(text);
            p.StandardInput.Close();
            double localPeak = 0;
            while (!p.WaitForExit(15))
            {
                try { p.Refresh(); localPeak = Math.Max(localPeak, p.PeakWorkingSet64 / 1048576.0); } catch { }
            }
            if (p.ExitCode != 0) throw new InvalidOperationException($"piper exit {p.ExitCode}");
            return (sw.Elapsed.TotalSeconds, localPeak);
        }

        // Cold start: process launch + model load + a one-word utterance.
        var cold = Run("Ready.", Path.Combine(Paths.Output, "tts", "_piper_cold.wav"));
        res.LoadSeconds = cold.Seconds;
        peak = Math.Max(peak, cold.PeakMb);
        res.Note = "Piper 2023.11.14-2 Windows binary as subprocess (one process per utterance: each call includes ~load time). RAM is the subprocess peak working set. LoadSeconds = cold start with a one-word utterance.";
        var (_, coldRate) = Wav.Read(File.ReadAllBytes(Path.Combine(Paths.Output, "tts", "_piper_cold.wav")));
        _ = coldRate;
        File.Delete(Path.Combine(Paths.Output, "tts", "_piper_cold.wav"));

        foreach (var (eventId, variant, text) in lines)
        {
            var path = WavPath(spec.Label, eventId, variant);
            var r = Run(text, path);
            peak = Math.Max(peak, r.PeakMb);
            var (samples, rate) = Wav.Read(File.ReadAllBytes(path));
            res.Lines.Add(new TtsLineResult { Voice = spec.Label, EventId = eventId, Variant = variant, Text = text, Wav = path, AudioSeconds = samples.Length / (double)rate, SynthSeconds = r.Seconds });
            Console.WriteLine($"  [{spec.Label}] {eventId}/{variant} {samples.Length / (double)rate:F2}s audio in {r.Seconds:F2}s wall");
        }
        res.PeakWorkingSetMb = peak;
        res.WorkingSetAfterLoadMb = peak;
    }

    // ---------------- parent ----------------
    public static async Task<int> RunAll(string[] args)
    {
        Directory.CreateDirectory(Paths.Output);
        var results = new List<TtsWorkerResult>();
        foreach (var engine in new[] { "kokoro", "piper" })
        {
            Console.WriteLine($"== TTS {engine}");
            var tmp = Path.Combine(Paths.Output, $"_tts_worker_{engine}.json");
            if (File.Exists(tmp)) File.Delete(tmp);
            var psi = new ProcessStartInfo(Environment.ProcessPath!, $"tts-worker --engine {engine} --out \"{tmp}\"") { UseShellExecute = false, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            results.Add(File.Exists(tmp)
                ? JsonSerializer.Deserialize<TtsWorkerResult>(File.ReadAllText(tmp))!
                : new TtsWorkerResult { Engine = engine, Error = $"worker exited {p.ExitCode} without result" });
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        // Radio variants (DSP in C#).
        foreach (var r in results.Where(r => r.Error == null))
            foreach (var l in r.Lines)
            {
                var (samples, rate) = Wav.Read(File.ReadAllBytes(l.Wav));
                l.RadioWav = l.Wav.Replace(".wav", "_radio.wav");
                Wav.Write16(l.RadioWav, RadioDsp.Process(samples, rate), rate);
            }

        File.WriteAllText(Path.Combine(Paths.Output, "tts_results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        WriteReport(results);
        return 0;
    }

    static void WriteReport(List<TtsWorkerResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PDA announcer TTS results");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}. CPU only. RTF = synthesis seconds / audio seconds (lower is faster; < 1 is faster than real time).");
        sb.AppendLine();
        sb.AppendLine("| Engine | Voices | Load s | RAM after load MB | Peak RAM MB | Mean RTF | Worst RTF | Notes |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            if (r.Error != null) { sb.AppendLine($"| {r.Engine} | | FAILED: {r.Error} | | | | | |"); continue; }
            var rtfs = r.Lines.Select(l => l.Rtf).ToList();
            var voices = string.Join(", ", r.Lines.Select(l => l.Voice).Distinct());
            sb.AppendLine($"| {r.Engine} | {voices} | {Fmt.F(r.LoadSeconds, 2)} | {Fmt.F(r.WorkingSetAfterLoadMb, 0)} | {Fmt.F(r.PeakWorkingSetMb, 0)} | {Fmt.F(rtfs.Average(), 3)} | {Fmt.F(rtfs.Max(), 3)} | {r.Note} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Per-voice RTF");
        foreach (var g in results.Where(r => r.Error == null).SelectMany(r => r.Lines).GroupBy(l => l.Voice))
            sb.AppendLine($"- {g.Key}: mean RTF {Fmt.F(g.Average(l => l.Rtf), 3)}, mean synth {Fmt.F(g.Average(l => l.SynthSeconds), 2)} s for {Fmt.F(g.Average(l => l.AudioSeconds), 2)} s of audio");
        sb.AppendLine();
        sb.AppendLine("## WAV files (plain and radio variant)");
        foreach (var g in results.Where(r => r.Error == null).SelectMany(r => r.Lines).GroupBy(l => l.Voice))
        {
            sb.AppendLine();
            sb.AppendLine($"### {g.Key}");
            foreach (var l in g)
                sb.AppendLine($"- `{Path.GetRelativePath(Paths.Output, l.Wav)}` and `{Path.GetRelativePath(Paths.Output, l.RadioWav!)}` ({l.Variant}, {Fmt.F(l.AudioSeconds, 2)} s): {l.Text}");
        }
        File.WriteAllText(Path.Combine(Paths.Output, "tts_results.md"), sb.ToString());
        Console.WriteLine(sb.ToString().Split("## WAV files")[0]);
    }
}
