using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RingworldTechDemo.Pda;

namespace PdaVoicePrototype;

/// <summary>Native vision transport/backend/lifecycle checks; scene interpretation stays human-reviewed.</summary>
public static class PdaVisionSmoke
{
    private const string Context = "Synthetic isolated fixture, not a live game observation. Describe only the supplied current image when present. No health, oxygen, inventory or action telemetry is available. A previous question's image is not supplied to this question.";

    public static async Task<int> Run(string[] args)
    {
        string key = Cli.Value(args, "--model") ?? "gemma-3-4b-q4";
        if (key != "gemma-3-4b-q4") throw new ArgumentException("Vision smoke requires retained Gemma 3 4B.");
        string modeText = Cli.Value(args, "--memory") ?? "vram";
        PdaMemoryMode mode = modeText switch { "ram" => PdaMemoryMode.SystemRam, "vram" => PdaMemoryMode.GpuVram, _ => throw new ArgumentException("--memory must be ram or vram.") };
        int totalBudgetSeconds = mode == PdaMemoryMode.SystemRam ? 300 : 180;
        int requestDeadlineSeconds = mode == PdaMemoryMode.SystemRam ? 75 : 25;
        int expectedRequestCount = mode == PdaMemoryMode.SystemRam ? 3 : 7;
        int layers = Number(args, "--gpu-layers", 16, 1, 99), threads = Number(args, "--threads", 4, 1, 16);
        string label = Cli.Value(args, "--label") ?? "pda_memory";
        if (!Regex.IsMatch(label, "^[A-Za-z0-9_-]{1,64}$")) throw new ArgumentException("Use a short filename-safe label.");
        string root = Directory.GetParent(Directory.GetParent(Paths.Root)!.FullName)!.FullName;
        string basePath = Path.Combine(root, ".godot", "pda_memory", "vision-smoke");
        string fixture = Path.Combine(basePath, Guid.NewGuid().ToString("N"));
        string reportDirectory = Path.Combine(basePath, "reports", label, DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(fixture);
        Directory.CreateDirectory(reportDirectory);
        string redPath = Path.Combine(fixture, "red-panel.png"), bluePath = Path.Combine(fixture, "blue-panel.png");
        var report = new VisionReport
        {
            ModelKey = key, MemoryMode = modeText, RequestedGpuLayers = mode == PdaMemoryMode.GpuVram ? layers : 0, Threads = threads,
            TotalBudgetSeconds = totalBudgetSeconds, RequestDeadlineSeconds = requestDeadlineSeconds, ExpectedRequestCount = expectedRequestCount,
            PlannedSequence = new[] { "image_a_red", "text_without_image", "image_b_blue", "repeat_1_red", "repeat_1_blue", "repeat_2_red", "repeat_2_blue" }.Take(expectedRequestCount).ToArray()
        };
        var timer = Stopwatch.StartNew();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(totalBudgetSeconds));
        using var client = new LocalPdaQaClient(root, key, threads, mode, layers, useComparisonHelper: true);
        using var sampler = new PdaModelComparison.ComparisonSampler(client, timer);
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; lifetime.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            using (JsonDocument candidates = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "resources", "pda", "model_artifacts.json"), lifetime.Token)))
            {
                JsonElement modelMetadata = candidates.RootElement.EnumerateArray().Single(c => c.GetProperty("Key").GetString() == key);
                report.ModelArtifact = await VerifyArtifact(modelMetadata, PdaModelCatalog.Get(key).RelativeFile, lifetime.Token);
            }
            using (JsonDocument projector = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "resources", "pda", "gemma_vision_projector.json"), lifetime.Token)))
            {
                JsonElement metadata = projector.RootElement;
                if (metadata.GetProperty("Bytes").GetInt64() != 851251104 ||
                    !string.Equals(metadata.GetProperty("Sha256").GetString(), "8c0fb064b019a6972856aaae2c7e4792858af3ca4561be2dbf649123ba6c40cb", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Projector metadata does not match the approved Gemma projector pin.");
                report.ProjectorArtifact = await VerifyArtifact(metadata, "_comparison/mmproj-model-f16.gguf", lifetime.Token);
            }
            byte[] red = PanelPng(240, 20, 20), blue = PanelPng(20, 20, 240);
            await File.WriteAllBytesAsync(redPath, red, lifetime.Token);
            await File.WriteAllBytesAsync(bluePath, blue, lifetime.Token);
            report.RedFixtureSha256 = Convert.ToHexString(SHA256.HashData(red));
            report.BlueFixtureSha256 = Convert.ToHexString(SHA256.HashData(blue));
            var captured = DateTimeOffset.FromUnixTimeSeconds(1000);
            var redImage = new PdaImageInput(red, captured, 1);
            var blueImage = new PdaImageInput(blue, captured, 2);
            using (var preCancelled = new CancellationTokenSource())
            {
                preCancelled.Cancel();
                bool rejected = false;
                try { await client.AskAsync("Describe the panel.", Context, preCancelled.Token, redImage); }
                catch (OperationCanceledException) { rejected = true; }
                report.PreCancelledDidNotStartHelper = rejected && client.WorkerProcessId == null;
                Check(report.PreCancelledDidNotStartHelper, "pre-cancelled request started a helper or was not cancelled");
            }
            await sampler.CaptureBaselineAsync();
            sampler.Start();
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                startup.CancelAfter(TimeSpan.FromSeconds(75));
                await client.WarmAsync(startup.Token);
            }
            sampler.TrackCurrent();
            report.Backend = client.BackendInfo;
            report.BackendVerified = report.Backend is { TotalLayers: > 0 } backend && (mode == PdaMemoryMode.SystemRam
                ? backend.Backend.Equals("cpu", StringComparison.OrdinalIgnoreCase) && backend.OffloadedLayers == 0
                : backend.Backend.Equals("cuda", StringComparison.OrdinalIgnoreCase) && backend.OffloadedLayers == Math.Min(layers, backend.TotalLayers) && !string.IsNullOrWhiteSpace(backend.Device));
            Check(report.BackendVerified, "language model backend does not match requested placement");
            int? initialPid = client.WorkerProcessId;
            Check(initialPid != null, "owned helper PID missing");
            var sequence = new (string Id, PdaImageInput? Image, string Question)[]
            {
                ("image_a_red", redImage, "Describe the dominant color and visible content of this current image in one sentence."),
                ("text_without_image", null, "Is a current camera image supplied to this question? State the limitation in one sentence."),
                ("image_b_blue", blueImage, "Describe the dominant color and visible content of this current image in one sentence."),
                ("repeat_1_red", redImage, "Describe this current image briefly."),
                ("repeat_1_blue", blueImage, "Describe this current image briefly."),
                ("repeat_2_red", redImage, "Describe this current image briefly."),
                ("repeat_2_blue", blueImage, "Describe this current image briefly.")
            };
            foreach (var request in sequence.Take(expectedRequestCount))
            {
                lifetime.Token.ThrowIfCancellationRequested();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(requestDeadlineSeconds));
                var watch = Stopwatch.StartNew();
                PdaQaReply reply = await client.AskAsync(request.Question, Context, deadline.Token, request.Image);
                sampler.TrackCurrent();
                var row = new VisionRow(request.Id, request.Question, request.Image != null, client.WorkerProcessId, watch.Elapsed.TotalMilliseconds, reply);
                report.Requests.Add(row);
                Save(report, sampler, reportDirectory);
                Check(reply.Error == null, request.Id + ": " + reply.Error);
                Check(client.WorkerProcessId == initialPid, "owned resident helper changed between requests");
                Check(reply.ImageUsed == (request.Image != null), "image/text protocol mismatch");
                if (request.Image != null)
                {
                    Check(reply.ImageCapturedAtUtc == request.Image.CapturedAtUtc && reply.ImageSnapshotVersion == request.Image.SnapshotVersion, "image provenance mismatch");
                    Check(reply.ImagePromptTokenPositions > 0 && reply.ImagePromptTokenPositions <= 4096 - 160 - 16 && reply.ImagePromptTokens > 0, "invalid multimodal prompt capacity metrics");
                    Check(string.Equals(reply.ProjectorBackend, mode == PdaMemoryMode.GpuVram ? "cuda" : "cpu", StringComparison.OrdinalIgnoreCase), "projector backend does not match requested mode");
                    Check(!string.IsNullOrWhiteSpace(reply.ProjectorDevice), "projector device evidence unavailable");
                }
                Console.WriteLine($"Vision smoke {request.Id}: {row.WallMilliseconds:0} ms; ImageUsed={reply.ImageUsed}; interpretation requires human review.");
            }
            report.HelperReused = true;
        }
        catch (Exception exception)
        {
            report.Error = exception.GetType().Name + ": " + exception.Message;
            Console.Error.WriteLine(report.Error);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            sampler.TrackCurrent();
            await sampler.StopAsync();
            report.DiagnosticTail = client.DiagnosticTail;
            client.Dispose();
            report.OwnedHelperExitConfirmed = await sampler.ConfirmExitAsync();
            report.TotalWallMilliseconds = timer.Elapsed.TotalMilliseconds;
            report.ProtocolBackendLifecyclePassed = report.Error == null && report.Requests.Count == expectedRequestCount && report.PreCancelledDidNotStartHelper && report.BackendVerified && report.HelperReused && report.OwnedHelperExitConfirmed;
            Save(report, sampler, reportDirectory);
            // Only these fixture files belong to this invocation. Reports remain available for review.
            File.Delete(redPath);
            File.Delete(bluePath);
            if (Directory.Exists(fixture) && !Directory.EnumerateFileSystemEntries(fixture).Any()) Directory.Delete(fixture);
        }
        Console.WriteLine("PDA_VISION_SMOKE=" + (report.ProtocolBackendLifecyclePassed ? "PASS" : "FAIL"));
        Console.WriteLine("Protocol/backend/lifecycle only; image answers require human review. Report: " + Path.Combine(reportDirectory, "results.json"));
        return report.ProtocolBackendLifecyclePassed ? 0 : 1;
    }

    private static void Save(VisionReport report, PdaModelComparison.ComparisonSampler sampler, string directory)
    {
        report.Samples = sampler.Samples;
        report.HelperProcesses = sampler.Processes;
        report.ObservedPeakWorkingSetBytes = report.Samples.Max(s => s.WorkingSetBytes) ?? 0;
        report.ObservedPeakPrivateBytes = report.Samples.Max(s => s.PrivateBytes) ?? 0;
        report.OsPeakWorkingSetBytes = report.Samples.Max(s => s.OsPeakWorkingSetBytes) ?? 0;
        foreach (var sample in report.Samples)
            foreach (string line in (sample.NvidiaDeviceWideCsv ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] columns = line.Split(',');
                if (columns.Length < 5 || !long.TryParse(columns[^3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long used)) continue;
                string index = columns[0].Trim();
                if (sample.Phase == "before helper startup") report.DeviceWideBaselineMiBByIndex[index] = used;
                if (!report.DeviceWideObservedPeakMiBByIndex.TryGetValue(index, out long old) || used > old) report.DeviceWideObservedPeakMiBByIndex[index] = used;
            }
        string path = Path.Combine(directory, "results.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }

    private static byte[] PanelPng(byte red, byte green, byte blue)
    {
        const int width = 32, height = 32;
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8; header[9] = 2;
        Chunk(png, "IHDR", header);
        using var pixels = new MemoryStream();
        using (var zlib = new ZLibStream(pixels, CompressionLevel.SmallestSize, true))
            for (int y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                for (int x = 0; x < width; x++) { zlib.WriteByte(red); zlib.WriteByte(green); zlib.WriteByte(blue); }
            }
        Chunk(png, "IDAT", pixels.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static async Task<VerifiedArtifact> VerifyArtifact(JsonElement metadata, string expectedRelativePath, CancellationToken token)
    {
        string relative = metadata.GetProperty("RelativePath").GetString() ?? throw new InvalidDataException("Artifact path missing.");
        string path = Path.GetFullPath(Path.Combine(Paths.Models, relative));
        if (!path.StartsWith(Path.GetFullPath(Paths.Models) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(path, Path.GetFullPath(Path.Combine(Paths.Models, expectedRelativePath)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Artifact metadata escapes the model directory or disagrees with the expected model/projector file.");
        PdaImageProtocol.EnsureNoReparsePoints(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Pinned model/projector is missing; vision smoke never downloads files.", path);
        long bytes = new FileInfo(path).Length;
        await using FileStream stream = File.OpenRead(path);
        string sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
        if (bytes != metadata.GetProperty("Bytes").GetInt64() || !string.Equals(sha256, metadata.GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installed model/projector differs from its pinned SHA-256 or size.");
        return new VerifiedArtifact(path, bytes, sha256, metadata.Clone());
    }

    private static void Chunk(Stream target, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        target.Write(number);
        byte[] tag = Encoding.ASCII.GetBytes(type);
        target.Write(tag); target.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte value in tag.Concat(data))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        target.Write(number);
    }

    private static int Number(string[] args, string key, int fallback, int min, int max)
    {
        int value = int.Parse(Cli.Value(args, key) ?? fallback.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return value >= min && value <= max ? value : throw new ArgumentOutOfRangeException(key);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private sealed record VisionRow(string Id, string Question, bool ImageRequested, int? WorkerProcessId, double WallMilliseconds, PdaQaReply Reply);
    private sealed record VerifiedArtifact(string Path, long Bytes, string Sha256, JsonElement Metadata);
    private sealed class VisionReport
    {
        public string ModelKey { get; set; } = "";
        public string MemoryMode { get; set; } = "";
        public int RequestedGpuLayers { get; set; }
        public int Threads { get; set; }
        public int TotalBudgetSeconds { get; set; }
        public int RequestDeadlineSeconds { get; set; }
        public int StartupDeadlineSeconds { get; } = 75;
        public int ExpectedRequestCount { get; set; }
        public string[] PlannedSequence { get; set; } = Array.Empty<string>();
        public string FixtureDefinition { get; } = "32x32 solid RGB panels: A=(240,20,20), B=(20,20,240); answers are ungraded and require human review.";
        public string? RedFixtureSha256 { get; set; }
        public string? BlueFixtureSha256 { get; set; }
        public VerifiedArtifact? ModelArtifact { get; set; }
        public VerifiedArtifact? ProjectorArtifact { get; set; }
        public string? Error { get; set; }
        public string? DiagnosticTail { get; set; }
        public PdaBackendInfo? Backend { get; set; }
        public bool BackendVerified { get; set; }
        public bool PreCancelledDidNotStartHelper { get; set; }
        public bool HelperReused { get; set; }
        public bool OwnedHelperExitConfirmed { get; set; }
        public bool ProtocolBackendLifecyclePassed { get; set; }
        public string HumanQualityVerdict { get; } = "requires_human_review";
        public string CancellationCoverage { get; } = "Pre-cancel only. Cancellation during native image loading/prefill is not exercised by this bounded smoke run.";
        public string MemoryInterpretation { get; } = "Sampled process host peaks and device-wide NVIDIA usage. Device-wide values include other processes. Repeated requests are not proof of absence of memory leaks or game FPS cost. Artifact SHA verification reads weights before startup; OS file cache is uncontrolled.";
        public double TotalWallMilliseconds { get; set; }
        public long ObservedPeakWorkingSetBytes { get; set; }
        public long ObservedPeakPrivateBytes { get; set; }
        public long OsPeakWorkingSetBytes { get; set; }
        public Dictionary<string, long> DeviceWideBaselineMiBByIndex { get; } = new();
        public Dictionary<string, long> DeviceWideObservedPeakMiBByIndex { get; } = new();
        public List<VisionRow> Requests { get; } = new();
        public IReadOnlyList<PdaModelComparison.MemorySample> Samples { get; set; } = Array.Empty<PdaModelComparison.MemorySample>();
        public IReadOnlyList<PdaModelComparison.HelperProcess> HelperProcesses { get; set; } = Array.Empty<PdaModelComparison.HelperProcess>();
    }
}
