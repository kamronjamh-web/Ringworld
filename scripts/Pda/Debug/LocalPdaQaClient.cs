using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RingworldTechDemo.Pda;

public enum PdaMemoryMode { SystemRam, GpuVram }
public sealed record PdaBackendInfo(string MemoryMode, string Backend, string Device, int OffloadedLayers, int TotalLayers);
public sealed record PdaQaReply(string Answer, string Model, double ElapsedMilliseconds, string? Error = null)
{
    public int PromptTokens { get; init; }
    public int OutputPieces { get; init; }
    public double FirstTokenMilliseconds { get; init; }
    public double QueueMilliseconds { get; init; }
    public double ModelReadyMilliseconds { get; init; }
    public double PrefillMilliseconds { get; init; }
    public double DecodeMilliseconds { get; init; }
}

/// <summary>Owns one resident local helper. All model work and protocol I/O happen away from the game thread.</summary>
public sealed class LocalPdaQaClient : IDisposable
{
    public const int MaxQuestionChars = 512, MaxContextChars = 10000;
    private readonly string _projectRoot, _modelKey;
    private readonly int _threads;
    private readonly PdaMemoryMode _memoryMode;
    private PdaBackendInfo? _backendInfo;
    private readonly object _processLock = new(), _diagnosticLock = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly StringBuilder _diagnostics = new();
    private Process? _process;
    private Task<Process>? _readiness;
    private volatile string _status = "Local model idle";
    private volatile bool _disposed;
    private long _requestId;
    private int _generation;

    public string Status => _status;
    public PdaBackendInfo? BackendInfo => Volatile.Read(ref _backendInfo);
    internal long LastRequestId => Interlocked.Read(ref _requestId);
    internal string DiagnosticTail { get { lock (_diagnosticLock) return _diagnostics.ToString(); } }
    internal int? WorkerProcessId
    {
        get
        {
            lock (_processLock)
            {
                try { return _process is { HasExited: false } ? _process.Id : null; }
                catch (InvalidOperationException) { return null; }
            }
        }
    }

    public LocalPdaQaClient(string projectRoot, string modelKey = "qwen3-0.6b", int threads = 4, PdaMemoryMode memoryMode = PdaMemoryMode.SystemRam)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _modelKey = modelKey;
        _threads = Math.Clamp(threads, 1, 16);
        _memoryMode = memoryMode;
        if (memoryMode is not (PdaMemoryMode.SystemRam or PdaMemoryMode.GpuVram)) throw new ArgumentOutOfRangeException(nameof(memoryMode));
        if (modelKey is not ("qwen3-0.6b" or "qwen2-0.5b" or "qwen2.5-0.5b"))
            throw new ArgumentException("Unknown local PDA model.", nameof(modelKey));
    }

    public Task<PdaQaReply> AskAsync(string question, string context, CancellationToken token)
    {
        int generation = Volatile.Read(ref _generation);
        return Task.Run(() => AskCoreAsync(question, context, generation, token), CancellationToken.None);
    }

    public Task WarmAsync(CancellationToken token = default)
    {
        int generation = Volatile.Read(ref _generation);
        return Task.Run(async () =>
        {
            await _requestGate.WaitAsync(token).ConfigureAwait(false);
            try { await GetReadyProcessAsync(generation, token).ConfigureAwait(false); }
            finally { _requestGate.Release(); }
        }, CancellationToken.None);
    }

    private async Task<PdaQaReply> AskCoreAsync(string question, string context, int generation, CancellationToken token)
    {
        bool entered = false;
        Process? process = null;
        double queueMs = 0, modelReadyMs = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            if (_disposed) throw new ObjectDisposedException(nameof(LocalPdaQaClient));
            question = question?.Trim() ?? "";
            context = context?.Trim() ?? "";
            if (question.Length == 0 || question.Length > MaxQuestionChars)
                throw new InvalidDataException($"Enter a question of 1 to {MaxQuestionChars} characters.");
            if (context.Length == 0 || context.Length > MaxContextChars)
                throw new InvalidDataException($"Prepared context must contain 1 to {MaxContextChars} characters.");
            var phaseTimer = Stopwatch.StartNew();
            await _requestGate.WaitAsync(token).ConfigureAwait(false);
            queueMs = phaseTimer.Elapsed.TotalMilliseconds;
            entered = true;
            if (_disposed || generation != Volatile.Read(ref _generation)) throw new OperationCanceledException("PDA helper stopped.", token);
            phaseTimer.Restart();
            process = await GetReadyProcessAsync(generation, token).ConfigureAwait(false);
            modelReadyMs = phaseTimer.Elapsed.TotalMilliseconds;
            token.ThrowIfCancellationRequested();
            long id = Interlocked.Increment(ref _requestId);
            string request = JsonSerializer.Serialize(new { type = "ask", id, question, context });
            _status = "Answering with " + _modelKey;
            using var responseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.StandardInput.WriteLineAsync(request.AsMemory(), responseTimeout.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(responseTimeout.Token).ConfigureAwait(false);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = token.Register(() => cancelled.TrySetResult());
            Task<JsonDocument> responseTask = ReadMessageAsync(process.StandardOutput, responseTimeout.Token);
            Task completed = await Task.WhenAny(responseTask, cancelled.Task).ConfigureAwait(false);
            if (completed == cancelled.Task)
            {
                _status = "Cancelling question; keeping model loaded";
                string cancel = JsonSerializer.Serialize(new { type = "cancel", id });
                await process.StandardInput.WriteLineAsync(cancel.AsMemory(), responseTimeout.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(responseTimeout.Token).ConfigureAwait(false);
                // Keep consuming the one terminal reply before allowing the next request to use this pipe.
                // A helper that cannot stop cooperatively gets terminated by this bounded watchdog.
                try { using JsonDocument discarded = await responseTask.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false); ValidateId(discarded.RootElement, id); }
                catch
                {
                    StopOwned(process);
                    responseTimeout.Cancel();
                    try { await responseTask.ConfigureAwait(false); } catch { }
                    throw;
                }
                _status = "Ready: " + _modelKey;
                throw new OperationCanceledException(token);
            }
            using JsonDocument response = await responseTask.ConfigureAwait(false);
            JsonElement answer = response.RootElement;
            ValidateId(answer, id);
            token.ThrowIfCancellationRequested();
            string? type = answer.GetProperty("type").GetString();
            if (type == "error")
            {
                _status = "Answer rejected";
                return new PdaQaReply("", _modelKey, 0, answer.GetProperty("message").GetString() ?? "Local PDA answer failed.")
                { QueueMilliseconds = queueMs, ModelReadyMilliseconds = modelReadyMs };
            }
            if (type != "answer") throw new InvalidDataException("Unexpected PDA worker response type.");
            string text = answer.GetProperty("text").GetString() ?? "";
            double elapsed = answer.GetProperty("elapsedMs").GetDouble();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 1400 || !double.IsFinite(elapsed) || elapsed < 0)
                throw new InvalidDataException("Local PDA answer was malformed.");
            _status = "Ready: " + _modelKey;
            return new PdaQaReply(text, _modelKey, elapsed)
            {
                PromptTokens = OptionalCount(answer, "promptTokens"), OutputPieces = OptionalCount(answer, "outputPieces"),
                FirstTokenMilliseconds = OptionalMilliseconds(answer, "firstTokenMilliseconds"),
                PrefillMilliseconds = OptionalMilliseconds(answer, "prefillMilliseconds"), DecodeMilliseconds = OptionalMilliseconds(answer, "decodeMilliseconds"),
                QueueMilliseconds = queueMs, ModelReadyMilliseconds = modelReadyMs
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || generation != Volatile.Read(ref _generation) || _disposed)
        {
            if (generation == Volatile.Read(ref _generation) && !_disposed && process != null)
                _status = "Ready: " + _modelKey;
            throw;
        }
        catch (Exception error)
        {
            if (process != null) StopOwned(process);
            _status = error is OperationCanceledException or TimeoutException ? "Local model timed out" : "Local model unavailable";
            string detail = error is OperationCanceledException or TimeoutException ? "The local PDA helper timed out. Try again or ask a shorter question." : error.Message;
            if (_memoryMode == PdaMemoryMode.GpuVram && error is EndOfStreamException)
                detail = "The GPU VRAM helper stopped. Check the CUDA 12 runtime and available GPU memory, or select system RAM. " + detail;
            return new PdaQaReply("", _modelKey, 0, detail) { QueueMilliseconds = queueMs, ModelReadyMilliseconds = modelReadyMs };
        }
        finally
        {
            if (entered) _requestGate.Release();
        }
    }

    private async Task<Process> GetReadyProcessAsync(int generation, CancellationToken token)
    {
        Task<Process> readiness;
        lock (_processLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LocalPdaQaClient));
            if (generation != _generation) throw new OperationCanceledException("PDA helper stopped.", token);
            if (_process == null || _process.HasExited)
            {
                _process?.Dispose();
                _process = null;
                ProcessStartInfo info = CreateStartInfo();
                var created = new Process { StartInfo = info };
                try
                {
                    if (!created.Start()) throw new IOException("Could not start the local PDA helper.");
                    _process = created;
                }
                catch { created.Dispose(); throw; }
                lock (_diagnosticLock) _diagnostics.Clear();
                _ = DrainDiagnosticsAsync(created.StandardError);
                _status = "Loading " + _modelKey + (_memoryMode == PdaMemoryMode.GpuVram ? " in GPU VRAM" : " in system RAM");
                _readiness = ReadReadyAsync(created, generation);
            }
            readiness = _readiness!;
        }
        // Cancelling a caller's wait must not interrupt or partially consume the helper's ready envelope.
        return await readiness.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task<Process> ReadReadyAsync(Process process, int generation)
    {
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using JsonDocument ready = await ReadMessageAsync(process.StandardOutput, startup.Token).ConfigureAwait(false);
            JsonElement root = ready.RootElement;
            if (root.GetProperty("type").GetString() == "error") throw new IOException(root.GetProperty("message").GetString());
            if (root.GetProperty("type").GetString() != "ready" || root.GetProperty("protocol").GetInt32() != 1 || root.GetProperty("model").GetString() != _modelKey)
                throw new InvalidDataException("Unexpected PDA worker protocol. Run tools/build_pda_worker.ps1.");
            PdaBackendInfo backend;
            if (root.TryGetProperty("memoryMode", out JsonElement mode))
            {
                backend = new PdaBackendInfo(mode.GetString() ?? "", root.GetProperty("backend").GetString() ?? "",
                    root.GetProperty("device").GetString() ?? "", root.GetProperty("offloadedLayers").GetInt32(), root.GetProperty("totalLayers").GetInt32());
                bool gpu = _memoryMode == PdaMemoryMode.GpuVram;
                if (backend.MemoryMode != (gpu ? "vram" : "ram") || backend.Backend != (gpu ? "cuda" : "cpu") || string.IsNullOrWhiteSpace(backend.Device)
                    || backend.OffloadedLayers < 0 || backend.TotalLayers < 0 || backend.OffloadedLayers > backend.TotalLayers
                    || (gpu && (backend.OffloadedLayers == 0 || backend.TotalLayers == 0)) || (!gpu && backend.OffloadedLayers != 0))
                    throw new InvalidDataException("PDA helper did not verify the requested memory/backend mode. Select RAM or check the CUDA runtime and available VRAM.");
            }
            else
            {
                if (_memoryMode != PdaMemoryMode.SystemRam) throw new InvalidDataException("GPU VRAM mode needs the new helper. Run tools/build_pda_worker.ps1.");
                backend = new PdaBackendInfo("ram", "cpu", "CPU (legacy helper)", 0, 0);
            }
            if (_disposed || generation != Volatile.Read(ref _generation)) throw new OperationCanceledException("PDA helper stopped.");
            Volatile.Write(ref _backendInfo, backend);
            _status = "Ready: " + _modelKey;
            return process;
        }
        catch (Exception error)
        {
            StopOwned(process);
            _status = "Local model unavailable";
            if (_memoryMode == PdaMemoryMode.GpuVram && error is not OperationCanceledException)
                throw new IOException("GPU VRAM helper startup failed. Check the CUDA 12 runtime and available GPU memory, or select system RAM. " + error.Message, error);
            throw;
        }
    }

    private static void ValidateId(JsonElement message, long id)
    {
        if (message.GetProperty("id").GetInt64() != id)
            throw new InvalidDataException("PDA worker response id did not match the question.");
        string? type = message.GetProperty("type").GetString();
        if (type is not ("answer" or "error" or "cancelled")) throw new InvalidDataException("Unexpected PDA worker response type.");
    }

    private static double OptionalMilliseconds(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value)) return 0;
        double number = value.GetDouble();
        if (!double.IsFinite(number) || number < 0) throw new InvalidDataException("Malformed PDA timing telemetry: " + property);
        return number;
    }

    private static int OptionalCount(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value)) return 0;
        int number = value.GetInt32();
        if (number < 0) throw new InvalidDataException("Malformed PDA count telemetry: " + property);
        return number;
    }

    private ProcessStartInfo CreateStartInfo()
    {
        string prototype = Path.Combine(_projectRoot, "prototypes", "pda-voice");
        string executable = Path.Combine(prototype, "bin", "qa-runtime", "PdaVoicePrototype.exe");
        if (!File.Exists(executable))
        {
            if (_memoryMode == PdaMemoryMode.GpuVram) throw new FileNotFoundException("GPU VRAM mode needs the new helper. Run tools/build_pda_worker.ps1.", executable);
            executable = Path.Combine(prototype, "bin", "Release", "net10.0", "win-x64", "PdaVoicePrototype.exe");
        }
        string dotnet = Path.Combine(_projectRoot, "artifacts", "engine-upgrade", "dotnet");
        if (!File.Exists(executable)) throw new FileNotFoundException("Build the local helper first: tools/build_pda_worker.ps1.", executable);
        if (!File.Exists(Path.Combine(dotnet, "dotnet.exe"))) throw new DirectoryNotFoundException("The pinned .NET 10 runtime is missing from artifacts/engine-upgrade/dotnet.");
        string modelFile = _modelKey switch
        {
            "qwen2-0.5b" => "qwen2-0_5b-instruct-q4_k_m.gguf",
            "qwen2.5-0.5b" => "qwen2.5-0.5b-instruct-q4_k_m.gguf",
            _ => "Qwen3-0.6B-Q8_0.gguf"
        };
        if (!File.Exists(Path.Combine(prototype, "models", modelFile)))
            throw new FileNotFoundException("The selected local GGUF model is missing: " + modelFile);
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = prototype, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
        };
        info.ArgumentList.Add("qa-worker");
        info.ArgumentList.Add("--model"); info.ArgumentList.Add(_modelKey);
        info.ArgumentList.Add("--threads"); info.ArgumentList.Add(_threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--memory"); info.ArgumentList.Add(_memoryMode == PdaMemoryMode.GpuVram ? "vram" : "ram");
        info.Environment["DOTNET_ROOT"] = dotnet;
        info.Environment["DOTNET_ROOT_X64"] = dotnet;
        if (_memoryMode == PdaMemoryMode.GpuVram) ConfigureCudaRuntime(info);
        return info;
    }

    private void ConfigureCudaRuntime(ProcessStartInfo info)
    {
        string? toolkit = info.Environment.TryGetValue("CUDA_PATH", out string? configured) ? configured : null;
        if (!string.IsNullOrWhiteSpace(toolkit))
        {
            try
            {
                using JsonDocument version = JsonDocument.Parse(File.ReadAllText(Path.Combine(toolkit, "version.json")));
                string? number = version.RootElement.GetProperty("cuda").GetProperty("version").GetString();
                string bin = Path.Combine(toolkit, "bin");
                if (number?.StartsWith("12.", StringComparison.Ordinal) == true && HasCudaRuntime(bin))
                {
                    info.Environment["PATH"] = bin + Path.PathSeparator + info.Environment["PATH"];
                    return;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        foreach (string variant in new[] { "pda-expressive", "pda-expressive-fast" })
        {
            string runtime = Path.Combine(_projectRoot, "artifacts", variant, "venv", "Lib", "site-packages", "torch", "lib");
            if (!HasCudaRuntime(runtime)) continue;
            info.Environment["PATH"] = runtime + Path.PathSeparator + info.Environment["PATH"];
            // The helper's CUDA-12-only policy handles Windows discovery; this version also describes the portable runtime to child tools.
            info.Environment["CUDA_VERSION"] = "12.8";
            // An incompatible inherited toolkit must not override the explicit portable CUDA 12 runtime.
            info.Environment.Remove("CUDA_PATH");
            return;
        }
        throw new DirectoryNotFoundException("A CUDA 12 runtime is unavailable. Select system RAM, or provide a compatible CUDA_PATH/runtime; this debug feature does not install CUDA or modify global environment settings.");
    }

    private static bool HasCudaRuntime(string directory) => File.Exists(Path.Combine(directory, "cublas64_12.dll"))
        && File.Exists(Path.Combine(directory, "cublasLt64_12.dll")) && File.Exists(Path.Combine(directory, "cudart64_12.dll"));

    private static async Task<JsonDocument> ReadMessageAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var next = new char[1];
        while (await reader.ReadAsync(next.AsMemory(), token).ConfigureAwait(false) != 0)
        {
            if (next[0] == '\n') return JsonDocument.Parse(text.ToString());
            if (next[0] != '\r') text.Append(next[0]);
            if (text.Length > 8192) throw new InvalidDataException("PDA worker response exceeded the protocol size limit.");
        }
        throw new EndOfStreamException("Local PDA helper exited before returning a response. Rebuild prototypes/pda-voice in Release mode if needed.");
    }

    private async Task DrainDiagnosticsAsync(StreamReader reader)
    {
        try
        {
            var buffer = new char[1024];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
            {
                lock (_diagnosticLock)
                {
                    _diagnostics.Append(buffer, 0, count);
                    if (_diagnostics.Length > 32768) _diagnostics.Remove(0, _diagnostics.Length - 32768);
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    public void Stop()
    {
        Process? process;
        lock (_processLock)
        {
            _generation++;
            process = _process;
            _process = null;
            _readiness = null;
            Volatile.Write(ref _backendInfo, null);
        }
        if (process != null) StopOwned(process);
        _status = "Local model stopped";
    }

    private void StopOwned(Process process)
    {
        lock (_processLock)
        {
            if (ReferenceEquals(_process, process)) { _process = null; _readiness = null; Volatile.Write(ref _backendInfo, null); }
        }
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        finally { process.Dispose(); }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
