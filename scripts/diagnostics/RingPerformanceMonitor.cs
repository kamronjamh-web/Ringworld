using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Godot;

namespace RingworldTechDemo.Diagnostics;

public readonly record struct RingPerformanceRow(string Name, bool Worker, bool Gpu, int Count, double MeanMilliseconds, double P95Milliseconds, double MaxMilliseconds, double LastSeenSeconds, long SourceFrameId);
public readonly record struct RingPerformanceFrame(double Seconds, double FrameMilliseconds, double? ProcessMilliseconds, double? PhysicsMilliseconds, double? SetupMilliseconds, double? ViewportCpuMilliseconds, double? ViewportGpuMilliseconds, float SpeedMetersPerSecond = 0, int PendingTerrainUploads = -1, int ActiveTerrainWorkers = -1, int TreeShadowRenderers = -1, int ShadowAtlasSize = -1, float RenderScale = 1, int DrawCalls = -1, long ReportedVideoMemoryBytes = -1, long FrameId = -1, double? FarViewportCpuMilliseconds = null, double? FarViewportGpuMilliseconds = null, double? RegionalFarViewportCpuMilliseconds = null, double? RegionalFarViewportGpuMilliseconds = null, double? RenderPrimitives = null);
public readonly record struct RingPerformanceContext(string TerrainCoordinator, string ChunkGrid, string SmoothTerrain, string Trees, string Grass, string Water, string ShadowAtlas, string GraphicsPreset, float RenderScale, string MemoryLabel, float SpeedMetersPerSecond, int DrawCalls, long RenderPrimitives, long ReportedVideoMemoryBytes);
public sealed record RingPerformanceSnapshot(IReadOnlyList<RingPerformanceRow> Rows, IReadOnlyList<RingPerformanceFrame> History, RingPerformanceContext Context, long WorkingSetBytes, long ManagedHeapBytes, long AllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections, bool GpuAvailable, string Caveat, int DroppedSamples, double EffectiveHistorySeconds, long RetainedTelemetryBytes);

public partial class RingPerformanceMonitor : Node
{
    public const string UserPath = "user://ring_performance_telemetry.tres";
    [Export] public bool Enabled { get; set; } = true;
    [Export] public bool DetailedGpuEnabled { get; set; }
    [Export(PropertyHint.Range, "1,60,1")] public int HistorySeconds { get; set; } = 30;
    [Export(PropertyHint.Range, "2,16,1")] public int MemoryBudgetMiB { get; set; } = 4;
    [Export(PropertyHint.Range, "0.1,2,0.1")] public float RefreshSeconds { get; set; } = .2f;
    [Export] public NodePath TerrainCoordinatorPath { get; set; } = new("../TerrainLodCoordinator");
    [Export] public NodePath GridPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath SmoothTerrainPath { get; set; } = new("../SmoothTerrainGrid");
    [Export] public NodePath TreesPath { get; set; } = new("../BiomeDecorations");
    [Export] public NodePath GrassPath { get; set; } = new("../StandingGrass");
    [Export] public NodePath WaterPath { get; set; } = new("../WaterFftController");
    [Export] public NodePath GraphicsPath { get; set; } = new("../GraphicsQuality");
    private RingPerformanceFrame[] _frames = Array.Empty<RingPerformanceFrame>(); private RingPerformanceTelemetry.Completed[] _events = Array.Empty<RingPerformanceTelemetry.Completed>(), _gpuEvents = Array.Empty<RingPerformanceTelemetry.Completed>(); private readonly RingPerformanceTelemetry.Completed[] _drain = new RingPerformanceTelemetry.Completed[2048], _gpuDrain = new RingPerformanceTelemetry.Completed[2048];
    private int _frameHead, _frameCount, _eventHead, _eventCount, _gpuEventHead, _gpuEventCount, _dropped, _gpuDropped; private double _clock, _lastFrameStamp, _pollElapsed, _saveElapsed, _summaryElapsed;
    private RingPerformanceSnapshot? _cached; private string? _settingsPath, _reportDirectoryForTests; private bool _settingsDirty, _forcedGpu, _gpuAvailable; private bool? _testGpuAvailability; private Rid _viewport;
    private long _workingSet, _heap, _allocated; private int _g0, _g1, _g2; public string LastSettingsError { get; private set; } = "";
    private RingPerformanceContext _context = new("unavailable", "unavailable", "unavailable", "unavailable", "unavailable", "unavailable", "unavailable", "unavailable", 1, "computed memory estimated", 0, 0, 0, 0);
    public static double NowSeconds => RingPerformanceTelemetry.NowSeconds();
    public void InstallSettingsPathForTests(string isolatedPath) => _settingsPath = ValidateTestPath(isolatedPath);
    public void InstallReportDirectoryForTests(string isolatedPath) => _reportDirectoryForTests = ValidateTestPath(isolatedPath);
    private static string ValidateTestPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Diagnostics test path is required.", nameof(path));
        string full = Path.GetFullPath(ProjectSettings.GlobalizePath(path));
        string generated = Path.GetFullPath(ProjectSettings.GlobalizePath("user://generated"));
        string godot = Path.GetFullPath(ProjectSettings.GlobalizePath("res://.godot"));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool belowGenerated = full.StartsWith(generated + Path.DirectorySeparatorChar, comparison);
        bool belowGodot = full.StartsWith(godot + Path.DirectorySeparatorChar, comparison);
        if (!belowGenerated && !belowGodot) throw new ArgumentException("Test paths must be below user://generated or res://.godot.", nameof(path));
        return full;
    }
    public override void _Ready() { EnsureCapacity(); ApplySettings(); EnsureCapacity(); _clock = _lastFrameStamp = RingPerformanceTelemetry.NowSeconds(); RingPerformanceTelemetry.Configure(Enabled, _events.Length + _gpuEvents.Length); _viewport = GetViewport().GetViewportRid(); _gpuAvailable = !DisplayServer.GetName().Contains("headless", StringComparison.OrdinalIgnoreCase) && RenderingServer.GetRenderingDevice() != null; UpdateViewportMeasurements(0); RingPerformanceGpu.Configure(Enabled && DetailedGpuEnabled && _gpuAvailable); }
    public override void _ExitTree() { CancelFarRingCaptureOnExit(); ReleaseViewportMeasurements(); if (_settingsDirty) SaveSettings(); RingPerformanceGpu.Configure(false); RingPerformanceTelemetry.Configure(false); }
    public override void _Process(double delta)
    {
        double now = RingPerformanceTelemetry.NowSeconds(); double wallDelta = Math.Max(0, now - _lastFrameStamp); _lastFrameStamp = now; _clock = now; EnsureCapacity(); RingPerformanceTelemetry.Configure(Enabled, _events.Length + _gpuEvents.Length); RingPerformanceGpu.Configure(Enabled && DetailedGpuEnabled && _gpuAvailable); UpdateViewportMeasurements(delta); if (!Enabled) { _materialWriteBaselineStale = true; if (_settingsDirty) { _saveElapsed += delta; if (_saveElapsed >= 1) SaveSettings(); } return; }
        using var scope = RingPerformanceTelemetry.MeasureCpu("Diagnostics.Collection");
        UpdateContextForDiagnostics(delta);
        SampleMaterialWrites();
        UpdateCloudHistory(delta);
        var outerFar = _outerFarMeasurement.Read();
        var regionalFar = _regionalFarMeasurement.Read();
        AddFrame(new RingPerformanceFrame(_clock, wallDelta * 1000, Read(Performance.Monitor.TimeProcess) * 1000, Read(Performance.Monitor.TimePhysicsProcess) * 1000, RenderingServer.GetFrameSetupTimeCpu(), _gpuAvailable ? RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewport) : null, _gpuAvailable ? RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewport) : null, _context.SpeedMetersPerSecond, _framePendingTerrainUploads, _frameActiveTerrainWorkers, _frameTreeShadowRenderers, _frameShadowAtlasSize, _context.RenderScale, _context.DrawCalls, _context.ReportedVideoMemoryBytes, unchecked((long)Engine.GetProcessFrames()), outerFar.Cpu, outerFar.Gpu, regionalFar.Cpu, regionalFar.Gpu, Read(Performance.Monitor.RenderTotalPrimitivesInFrame)));
        if (RingPerformanceGpu.Enabled) RingPerformanceGpu.RequestReadback();
        int n = RingPerformanceTelemetry.Drain(_drain, out int dropped); _dropped += dropped; for (int i = 0; i < n; i++) AddEvent(_drain[i]);
        n = RingPerformanceTelemetry.DrainGpu(_gpuDrain, out dropped); _gpuDropped += dropped; for (int i = 0; i < n; i++) AddGpuEvent(_gpuDrain[i]);
        _pollElapsed += delta; _saveElapsed += delta; _summaryElapsed += delta; if (_pollElapsed >= 1) { _pollElapsed = 0; PollMemory(); _cached = null; }
        if (_settingsDirty && _saveElapsed >= 1) SaveSettings();
    }
    // Allocate most retention to high-frequency CPU events; pending queues stay separately capped.
    // The conservative 160-byte allowance covers retained samples, frames, and pending storage.
    private void EnsureCapacity() { MemoryBudgetMiB = Math.Clamp(MemoryBudgetMiB, 2, 16); HistorySeconds = Math.Clamp(HistorySeconds, 1, 60); RefreshSeconds = float.IsFinite(RefreshSeconds) ? Math.Clamp(RefreshSeconds, .1f, 2f) : .2f; long budget = MemoryBudgetMiB * 1024L * 1024L; long cloudReserve = MemoryBudgetMiB <= 2 ? 512L * 1024L : MemoryBudgetMiB <= 4 ? 1024L * 1024L : 3L * 1024L * 1024L; long fixedReserve = cloudReserve + LiveWorkspaceReserve + 32768L + ((long)_drain.Length + _gpuDrain.Length) * 64L; int total = (int)Math.Clamp((budget - fixedReserve) / 160L, 768L, 131072L); int frames = Math.Clamp(total / 8, 120, 8192), gpu = Math.Clamp(total / 8, 128, 8192), events = Math.Clamp(total - frames - gpu, 256, 131072); if (_frames.Length != frames) { _frames = new RingPerformanceFrame[frames]; _frameHead = _frameCount = 0; _cached = null; } if (_events.Length != events) { _events = new RingPerformanceTelemetry.Completed[events]; _eventHead = _eventCount = 0; _cached = null; } if (_gpuEvents.Length != gpu) { _gpuEvents = new RingPerformanceTelemetry.Completed[gpu]; _gpuEventHead = _gpuEventCount = 0; _cached = null; } }
    private void AddFrame(RingPerformanceFrame f) { if (_frameCount == _frames.Length) { _frameHead = (_frameHead + 1) % _frames.Length; _frameCount--; _dropped++; } _frames[(_frameHead + _frameCount) % _frames.Length] = f; _frameCount++; Trim(); _cached = null; }
    private void AddEvent(RingPerformanceTelemetry.Completed e) { if (_eventCount == _events.Length) { _eventHead = (_eventHead + 1) % _events.Length; _eventCount--; _dropped++; } _events[(_eventHead + _eventCount) % _events.Length] = e; _eventCount++; Trim(); _cached = null; }
    private void AddGpuEvent(RingPerformanceTelemetry.Completed e) { if (_gpuEventCount == _gpuEvents.Length) { _gpuEventHead = (_gpuEventHead + 1) % _gpuEvents.Length; _gpuEventCount--; _gpuDropped++; } _gpuEvents[(_gpuEventHead + _gpuEventCount) % _gpuEvents.Length] = e; _gpuEventCount++; Trim(); _cached = null; }
    private void Trim() { double cutoff = _clock - Math.Clamp(HistorySeconds, 1, 60); while (_frameCount > 0 && _frames[_frameHead].Seconds < cutoff) { _frameHead = (_frameHead + 1) % _frames.Length; _frameCount--; } while (_eventCount > 0 && _events[_eventHead].CompletedSeconds < cutoff) { _eventHead = (_eventHead + 1) % _events.Length; _eventCount--; } while (_gpuEventCount > 0 && _gpuEvents[_gpuEventHead].CompletedSeconds < cutoff) { _gpuEventHead = (_gpuEventHead + 1) % _gpuEvents.Length; _gpuEventCount--; } }
    private static double Read(Performance.Monitor m) => Performance.GetMonitor(m);
    private void PollMemory() { using Process p = Process.GetCurrentProcess(); _workingSet = p.WorkingSet64; _heap = GC.GetTotalMemory(false); _allocated = GC.GetTotalAllocatedBytes(false); _g0 = GC.CollectionCount(0); _g1 = GC.CollectionCount(1); _g2 = GC.CollectionCount(2); }
    public RingPerformanceSnapshot GetSnapshot() { using var summary = RingPerformanceTelemetry.MeasureCpu("Diagnostics.Summary"); if (_cached != null) return _cached; var groups = new Dictionary<(string, bool, bool), List<RingPerformanceTelemetry.Completed>>(); void Add(RingPerformanceTelemetry.Completed e) { var k = (e.Name, e.Worker, e.Gpu); if (!groups.TryGetValue(k, out var l)) groups[k] = l = new(); l.Add(e); } for (int i = 0; i < _eventCount; i++) Add(_events[(_eventHead + i) % _events.Length]); for (int i = 0; i < _gpuEventCount; i++) Add(_gpuEvents[(_gpuEventHead + i) % _gpuEvents.Length]); var rows = new List<RingPerformanceRow>(); foreach (var g in groups) { var last = g.Value[0]; foreach (var item in g.Value) if (item.CompletedSeconds > last.CompletedSeconds) last = item; g.Value.Sort((a, b) => a.Milliseconds.CompareTo(b.Milliseconds)); int p = (int)Math.Clamp(Math.Ceiling(g.Value.Count * .95) - 1, 0, g.Value.Count - 1); double sum = 0; foreach (var v in g.Value) sum += v.Milliseconds; rows.Add(new(g.Key.Item1, g.Key.Item2, g.Key.Item3, g.Value.Count, sum / g.Value.Count, g.Value[p].Milliseconds, g.Value[^1].Milliseconds, last.CompletedSeconds, last.SourceFrameId)); } var history = new RingPerformanceFrame[_frameCount]; for (int i = 0; i < _frameCount; i++) history[i] = _frames[(_frameHead + i) % _frames.Length]; double effective = _frameCount > 1 ? history[^1].Seconds - history[0].Seconds : 0; long retained = EstimatedRetainedBytes(); return _cached = new(rows, history, _context, _workingSet, _heap, _allocated, _g0, _g1, _g2, _testGpuAvailability ?? _gpuAvailable, "CPU rows are per-call wall durations; worker work is not frame-critical time. GPU values are delayed source-frame samples keyed by completion time and are never CPU-frame paired. Nested GPU scopes may overlap, so categories/source-frame groups are not summed. Diagnostics can drop samples; inspect v2 retention counters.", _dropped + _gpuDropped, effective, retained); }
    public void SetContextForDiagnostics(RingPerformanceContext context) { _context = context; _cached = null; }
    public void ClearHistory() { _liveSnapshot = null; _nextLiveSummary = 0; _frameHead = _frameCount = _eventHead = _eventCount = _gpuEventHead = _gpuEventCount = _dropped = _gpuDropped = 0; ClearCloudHistory(); ResetMaterialWrites(); RingPerformanceTelemetry.Configure(false); RingPerformanceTelemetry.Configure(Enabled, _events.Length + _gpuEvents.Length); _cached = null; }
    public void MarkSettingsDirty() => _settingsDirty = true;
    public void ResetSettings() { Enabled = true; DetailedGpuEnabled = false; HistorySeconds = 30; MemoryBudgetMiB = 4; RefreshSeconds = .2f; _settingsDirty = true; }
    public void ApplySettings() { string path = _settingsPath ?? UserPath; LastSettingsError = ""; try { if (!ResourceLoader.Exists(path)) return; var s = ResourceLoader.Load<RingPerformanceTelemetrySettings>(path, cacheMode: ResourceLoader.CacheMode.Ignore); if (s == null) throw new InvalidDataException("Invalid diagnostics settings"); Enabled = s.Enabled; DetailedGpuEnabled = s.DetailedGpuEnabled; HistorySeconds = Math.Clamp(s.HistorySeconds, 1, 60); MemoryBudgetMiB = Math.Clamp(s.MemoryBudgetMiB, 2, 16); RefreshSeconds = float.IsFinite(s.RefreshSeconds) ? Math.Clamp(s.RefreshSeconds, .1f, 2f) : .2f; } catch (Exception e) { LastSettingsError = e.Message; } }
    public bool SaveSettings()
    {
        string temp = "";
        try
        {
            string path = _settingsPath is null ? ProjectSettings.GlobalizePath(UserPath) : ValidateTestPath(_settingsPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp.tres";
            var settings = new RingPerformanceTelemetrySettings
            {
                Enabled = Enabled,
                DetailedGpuEnabled = DetailedGpuEnabled,
                HistorySeconds = Math.Clamp(HistorySeconds, 1, 60),
                MemoryBudgetMiB = Math.Clamp(MemoryBudgetMiB, 2, 16),
                RefreshSeconds = float.IsFinite(RefreshSeconds) ? Math.Clamp(RefreshSeconds, .1f, 2f) : .2f
            };
            if (ResourceSaver.Save(settings, temp) != Error.Ok) throw new IOException("Could not save diagnostics settings");
            File.Move(temp, path, true);
            _settingsDirty = false;
            _saveElapsed = 0;
            LastSettingsError = "";
            return true;
        }
        catch (Exception e) { LastSettingsError = e.Message; return false; }
        finally { if (temp.Length > 0) try { File.Delete(temp); } catch { } }
    }
    public string CaptureSlowdown() { try { string dir = _reportDirectoryForTests ?? RingGeneratedOutputPaths.Directory("reports/diagnostics"); Directory.CreateDirectory(dir); string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")[..8]; string path = Path.Combine(dir, "ring-performance-" + id + ".json"); var files = Directory.GetFiles(dir, "ring-performance-*.json"); long bytes = 0; foreach (string file in files) bytes += new FileInfo(file).Length; object? renderState = null; string? renderStateContextError = null; try { renderState = CaptureRenderState(); } catch (Exception captureError) { renderStateContextError = captureError.Message; } string json = JsonSerializer.Serialize(new { schemaVersion = 2, capturedUtc = DateTime.UtcNow.ToString("O"), metadata = CaptureMetadata(), attribution = CaptureAttribution(), snapshot = GetSnapshot(), events = EventCopy(), gpuEvents = GpuEventCopy(), captureOnlyOneSecondBins = CaptureBins(), gpuBySourceFrame = CaptureGpuSourceGroups(), cloudHistory = CopyCloudHistory(), cloudHistoryDiagnostics = CloudHistoryDiagnostics, renderState, renderStateContextError }, new JsonSerializerOptions { WriteIndented = true }); long added = System.Text.Encoding.UTF8.GetByteCount(json); const long cap = 64L * 1024 * 1024; if (files.Length >= 20 || added > cap || bytes + added > cap) return "Export refused: diagnostics report cap reached."; File.WriteAllText(path, json); return path; } catch (Exception e) { return "Export failed: " + e.Message; } }
    // Legacy export key remains a mixed chronological stream for existing tools.
    private RingPerformanceTelemetry.Completed[] EventCopy() { var a = new RingPerformanceTelemetry.Completed[_eventCount + _gpuEventCount]; int at = 0; for (int i = 0; i < _eventCount; i++) a[at++] = _events[(_eventHead + i) % _events.Length]; for (int i = 0; i < _gpuEventCount; i++) a[at++] = _gpuEvents[(_gpuEventHead + i) % _gpuEvents.Length]; Array.Sort(a, (x, y) => x.CompletedSeconds.CompareTo(y.CompletedSeconds)); return a; }
    private RingPerformanceTelemetry.Completed[] GpuEventCopy() { var a = new RingPerformanceTelemetry.Completed[_gpuEventCount]; for (int i = 0; i < _gpuEventCount; i++) a[i] = _gpuEvents[(_gpuEventHead + i) % _gpuEvents.Length]; return a; }
    public void InjectFrameForTests(double seconds, double frameMilliseconds) { _clock = Math.Max(_clock, seconds); AddFrame(new(seconds, frameMilliseconds, null, null, null, null, null)); _cached = null; }
    public void InjectCompletedSampleForTests(string name, double milliseconds, bool worker = false, bool gpu = false, long sourceFrameId = -1, double? completedSeconds = null) { var sample = new RingPerformanceTelemetry.Completed(name, worker, milliseconds, gpu, sourceFrameId, completedSeconds ?? _clock); if (gpu) AddGpuEvent(sample); else AddEvent(sample); _cached = null; }
    public void SetGpuAvailabilityForTests(bool available) { _testGpuAvailability = available; _cached = null; }
    public void ResetForTests() { ClearHistory(); _clock = _lastFrameStamp = _pollElapsed = _saveElapsed = 0; _cached = null; }
}
