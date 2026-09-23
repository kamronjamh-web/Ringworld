using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Godot;

namespace RingworldTechDemo.Diagnostics;

public readonly record struct RingPerformanceRow(string Name, bool Worker, bool Gpu, int Count, double MeanMilliseconds, double P95Milliseconds, double MaxMilliseconds, double LastSeenSeconds, long SourceFrameId);
public readonly record struct RingPerformanceFrame(double Seconds, double FrameMilliseconds, double? ProcessMilliseconds, double? PhysicsMilliseconds, double? SetupMilliseconds, double? ViewportCpuMilliseconds, double? ViewportGpuMilliseconds, float SpeedMetersPerSecond = 0, int PendingTerrainUploads = -1, int ActiveTerrainWorkers = -1, int TreeShadowRenderers = -1, int ShadowAtlasSize = -1, float RenderScale = 1, int DrawCalls = -1, long ReportedVideoMemoryBytes = -1);
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
    private RingPerformanceFrame[] _frames = Array.Empty<RingPerformanceFrame>(); private RingPerformanceTelemetry.Completed[] _events = Array.Empty<RingPerformanceTelemetry.Completed>(); private readonly RingPerformanceTelemetry.Completed[] _drain = new RingPerformanceTelemetry.Completed[64];
    private int _frameHead, _frameCount, _eventHead, _eventCount, _dropped; private double _clock, _lastFrameStamp, _pollElapsed, _saveElapsed, _summaryElapsed;
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
    public override void _Ready() { EnsureCapacity(); ApplySettings(); _clock = _lastFrameStamp = RingPerformanceTelemetry.NowSeconds(); RingPerformanceTelemetry.Configure(Enabled, _events.Length); _viewport = GetViewport().GetViewportRid(); _gpuAvailable = !DisplayServer.GetName().Contains("headless", StringComparison.OrdinalIgnoreCase) && RenderingServer.GetRenderingDevice() != null; if (_gpuAvailable && _viewport.IsValid) RenderingServer.ViewportSetMeasureRenderTime(_viewport, true); RingPerformanceGpu.Configure(Enabled && DetailedGpuEnabled && _gpuAvailable); }
    public override void _ExitTree() { if (_settingsDirty) SaveSettings(); RingPerformanceGpu.Configure(false); RingPerformanceTelemetry.Configure(false); }
    public override void _Process(double delta)
    {
        double now = RingPerformanceTelemetry.NowSeconds(); double wallDelta = Math.Max(0, now - _lastFrameStamp); _lastFrameStamp = now; _clock = now; EnsureCapacity(); RingPerformanceTelemetry.Configure(Enabled, _events.Length); RingPerformanceGpu.Configure(Enabled && DetailedGpuEnabled && _gpuAvailable); if (!Enabled) { if (_settingsDirty) { _saveElapsed += delta; if (_saveElapsed >= 1) SaveSettings(); } return; }
        using var scope = RingPerformanceTelemetry.MeasureCpu("Diagnostics.Collection");
        UpdateContextForDiagnostics(delta);
        AddFrame(new RingPerformanceFrame(_clock, wallDelta * 1000, Read(Performance.Monitor.TimeProcess) * 1000, Read(Performance.Monitor.TimePhysicsProcess) * 1000, RenderingServer.GetFrameSetupTimeCpu(), _gpuAvailable ? RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewport) : null, _gpuAvailable ? RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewport) : null, _context.SpeedMetersPerSecond, _framePendingTerrainUploads, _frameActiveTerrainWorkers, _frameTreeShadowRenderers, _frameShadowAtlasSize, _context.RenderScale, _context.DrawCalls, _context.ReportedVideoMemoryBytes));
        if (RingPerformanceGpu.Enabled) RingPerformanceGpu.RequestReadback();
        int n = RingPerformanceTelemetry.Drain(_drain, out int dropped); _dropped += dropped; for (int i = 0; i < n; i++) AddEvent(_drain[i]);
        _pollElapsed += delta; _saveElapsed += delta; _summaryElapsed += delta; if (_pollElapsed >= 1) { _pollElapsed = 0; PollMemory(); _cached = null; }
        if (_settingsDirty && _saveElapsed >= 1) SaveSettings();
    }
    private void EnsureCapacity() { MemoryBudgetMiB = Math.Clamp(MemoryBudgetMiB, 2, 16); HistorySeconds = Math.Clamp(HistorySeconds, 1, 60); RefreshSeconds = float.IsFinite(RefreshSeconds) ? Math.Clamp(RefreshSeconds, .1f, 2f) : .2f; int total = Math.Max(512, (MemoryBudgetMiB * 1024 * 1024 - 32768) / 256); int frames = Math.Clamp(total / 4, 120, 8192), events = Math.Clamp(total - frames, 256, 32768); if (_frames.Length != frames) { _frames = new RingPerformanceFrame[frames]; _frameHead = _frameCount = 0; _cached = null; } if (_events.Length != events) { _events = new RingPerformanceTelemetry.Completed[events]; _eventHead = _eventCount = 0; _cached = null; } }
    private void AddFrame(RingPerformanceFrame f) { if (_frameCount == _frames.Length) { _frameHead = (_frameHead + 1) % _frames.Length; _frameCount--; _dropped++; } _frames[(_frameHead + _frameCount) % _frames.Length] = f; _frameCount++; Trim(); _cached = null; }
    private void AddEvent(RingPerformanceTelemetry.Completed e) { if (_eventCount == _events.Length) { _eventHead = (_eventHead + 1) % _events.Length; _eventCount--; _dropped++; } _events[(_eventHead + _eventCount) % _events.Length] = e; _eventCount++; Trim(); _cached = null; }
    private void Trim() { double cutoff = _clock - Math.Clamp(HistorySeconds, 1, 60); while (_frameCount > 0 && _frames[_frameHead].Seconds < cutoff) { _frameHead = (_frameHead + 1) % _frames.Length; _frameCount--; } while (_eventCount > 0 && _events[_eventHead].CompletedSeconds < cutoff) { _eventHead = (_eventHead + 1) % _events.Length; _eventCount--; } }
    private static double Read(Performance.Monitor m) => Performance.GetMonitor(m);
    private void PollMemory() { using Process p = Process.GetCurrentProcess(); _workingSet = p.WorkingSet64; _heap = GC.GetTotalMemory(false); _allocated = GC.GetTotalAllocatedBytes(false); _g0 = GC.CollectionCount(0); _g1 = GC.CollectionCount(1); _g2 = GC.CollectionCount(2); }
    public RingPerformanceSnapshot GetSnapshot() { using var summary = RingPerformanceTelemetry.MeasureCpu("Diagnostics.Summary"); if (_cached != null) return _cached; var groups = new Dictionary<(string, bool, bool), List<RingPerformanceTelemetry.Completed>>(); for (int i = 0; i < _eventCount; i++) { var e = _events[(_eventHead + i) % _events.Length]; var k = (e.Name, e.Worker, e.Gpu); if (!groups.TryGetValue(k, out var l)) groups[k] = l = new(); l.Add(e); } var rows = new List<RingPerformanceRow>(); foreach (var g in groups) { var last = g.Value[0]; foreach (var item in g.Value) if (item.CompletedSeconds > last.CompletedSeconds) last = item; g.Value.Sort((a, b) => a.Milliseconds.CompareTo(b.Milliseconds)); int p = (int)Math.Clamp(Math.Ceiling(g.Value.Count * .95) - 1, 0, g.Value.Count - 1); double sum = 0; foreach (var v in g.Value) sum += v.Milliseconds; rows.Add(new(g.Key.Item1, g.Key.Item2, g.Key.Item3, g.Value.Count, sum / g.Value.Count, g.Value[p].Milliseconds, g.Value[^1].Milliseconds, last.CompletedSeconds, last.SourceFrameId)); } var history = new RingPerformanceFrame[_frameCount]; for (int i = 0; i < _frameCount; i++) history[i] = _frames[(_frameHead + i) % _frames.Length]; double effective = _frameCount > 1 ? history[^1].Seconds - history[0].Seconds : 0; long retained = (long)_frames.Length * 192L + (long)_events.Length * 64L + (long)Math.Min(_events.Length, 16384) * 64L + (long)_drain.Length * 64L + 32768; return _cached = new(rows, history, _context, _workingSet, _heap, _allocated, _g0, _g1, _g2, _testGpuAvailability ?? _gpuAvailable, "CPU rows are per-call wall durations; worker work is not frame-critical time. AllocatedBytes is process-lifetime cumulative. GPU values are delayed source-frame samples, never CPU-frame paired; main viewport only. Diagnostics overhead is partial.", _dropped, effective, retained); }
    public void SetContextForDiagnostics(RingPerformanceContext context) { _context = context; _cached = null; }
    public void ClearHistory() { _frameHead = _frameCount = _eventHead = _eventCount = _dropped = 0; RingPerformanceTelemetry.Configure(false); RingPerformanceTelemetry.Configure(Enabled, _events.Length); _cached = null; }
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
    public string CaptureSlowdown() { try { string dir = _reportDirectoryForTests ?? RingGeneratedOutputPaths.Directory("reports/diagnostics"); Directory.CreateDirectory(dir); string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")[..8]; string path = Path.Combine(dir, "ring-performance-" + id + ".json"); var files = Directory.GetFiles(dir, "ring-performance-*.json"); long bytes = 0; foreach (string file in files) bytes += new FileInfo(file).Length; string json = JsonSerializer.Serialize(new { snapshot = GetSnapshot(), events = EventCopy() }, new JsonSerializerOptions { WriteIndented = true }); long added = System.Text.Encoding.UTF8.GetByteCount(json); const long cap = 64L * 1024 * 1024; if (files.Length >= 20 || added > cap || bytes + added > cap) return "Export refused: diagnostics report cap reached."; File.WriteAllText(path, json); return path; } catch (Exception e) { return "Export failed: " + e.Message; } }
    private RingPerformanceTelemetry.Completed[] EventCopy() { var a = new RingPerformanceTelemetry.Completed[_eventCount]; for (int i = 0; i < _eventCount; i++) a[i] = _events[(_eventHead + i) % _events.Length]; return a; }
    public void InjectFrameForTests(double seconds, double frameMilliseconds) { _clock = Math.Max(_clock, seconds); AddFrame(new(seconds, frameMilliseconds, null, null, null, null, null)); _cached = null; }
    public void InjectCompletedSampleForTests(string name, double milliseconds, bool worker = false, bool gpu = false, long sourceFrameId = -1, double? completedSeconds = null) { AddEvent(new(name, worker, milliseconds, gpu, sourceFrameId, completedSeconds ?? _clock)); _cached = null; }
    public void SetGpuAvailabilityForTests(bool available) { _testGpuAvailability = available; _cached = null; }
    public void ResetForTests() { ClearHistory(); _clock = _lastFrameStamp = _pollElapsed = _saveElapsed = 0; _cached = null; }
}
