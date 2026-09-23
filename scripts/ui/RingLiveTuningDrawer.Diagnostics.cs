using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using RingworldTechDemo.Diagnostics;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private Label? _diagnosticsSummary, _diagnosticsRows, _diagnosticsStatus, _diagnosticsMemory;
    private RingPerformanceGraph? _diagnosticsCpuGraph, _diagnosticsGpuGraph;
    private OptionButton? _diagnosticsSort;
    private CheckButton? _diagnosticsMonitoringToggle, _diagnosticsGpuToggle, _diagnosticsOverlayToggle;
    private HSlider? _diagnosticsHistorySlider, _diagnosticsMemorySlider, _diagnosticsRefreshSlider;
    private readonly Dictionary<HSlider, Label> _diagnosticsSliderValues = new();
    private double _diagnosticsRefreshElapsed;

    private void BuildDiagnosticsTab(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Diagnostics"); bool available = _performanceMonitor != null;
        VBoxContainer monitor = TuningMenuUi.CreateSection(tab, "Performance monitoring", "Recording remains per-frame while this visible display refreshes at the selected rate.");
        _diagnosticsMonitoringToggle = Toggle(monitor, "DiagnosticsMonitoringToggle", "Enable performance monitoring", _performanceMonitor?.Enabled ?? false, v => { if (_performanceMonitor != null) { _performanceMonitor.Enabled = v; _performanceMonitor.MarkSettingsDirty(); } }, available);
        _diagnosticsGpuToggle = Toggle(monitor, "DiagnosticsGpuToggle", "Detailed GPU intervals", _performanceMonitor?.DetailedGpuEnabled ?? false, v => { if (_performanceMonitor != null) { _performanceMonitor.DetailedGpuEnabled = v; _performanceMonitor.MarkSettingsDirty(); } }, available);
        _diagnosticsOverlayToggle = Toggle(monitor, "DiagnosticsOverlayToggle", "Show F6 diagnostics overlay", _performanceHud?.Visible ?? false, v => { if (_performanceHud != null) { _performanceHud.Visible = v; if (v) _performanceHud.RefreshNow(); } }, _performanceHud != null);
        _diagnosticsHistorySlider = Slider(monitor, "DiagnosticsHistorySeconds", "History seconds", 5, 60, 1, _performanceMonitor?.HistorySeconds ?? 30, v => { if (_performanceMonitor != null) { _performanceMonitor.HistorySeconds = Mathf.RoundToInt(v); _performanceMonitor.MarkSettingsDirty(); } }, available);
        _diagnosticsMemorySlider = Slider(monitor, "DiagnosticsMemoryMiB", "Telemetry memory MiB", 2, 16, 1, _performanceMonitor?.MemoryBudgetMiB ?? 4, v => { if (_performanceMonitor != null) { _performanceMonitor.MemoryBudgetMiB = Mathf.RoundToInt(v); _performanceMonitor.MarkSettingsDirty(); } }, available);
        _diagnosticsRefreshSlider = Slider(monitor, "DiagnosticsDisplayRefresh", "Display refresh seconds", .1, 1, .1, _performanceMonitor?.RefreshSeconds ?? .2, v => { if (_performanceMonitor != null) { _performanceMonitor.RefreshSeconds = (float)v; _performanceMonitor.MarkSettingsDirty(); } }, available);
        VBoxContainer results = TuningMenuUi.CreateSection(tab, "Live results", "CPU main-thread elapsed, completed worker-job durations, and measured GPU intervals are independent and can overlap; never sum them. Per-object shadow and SSR costs are unavailable.");
        _diagnosticsSummary = new Label { Name = "DiagnosticsSummary", AutowrapMode = TextServer.AutowrapMode.WordSmart }; results.AddChild(_diagnosticsSummary);
        results.AddChild(new Label { Text = "Main viewport CPU render (ms)", TooltipText = "This is the main viewport render interval; far rendering is separate and uninstrumented.", AutowrapMode = TextServer.AutowrapMode.WordSmart }); _diagnosticsCpuGraph = Graph("DiagnosticsCpuGraph", RingPerformanceGraphMetric.Cpu); results.AddChild(_diagnosticsCpuGraph);
        results.AddChild(new Label { Text = "Main viewport GPU render (ms)", TooltipText = "This is the main viewport render interval; unsupported or disabled samples leave graph gaps.", AutowrapMode = TextServer.AutowrapMode.WordSmart }); _diagnosticsGpuGraph = Graph("DiagnosticsGpuGraph", RingPerformanceGraphMetric.Gpu); results.AddChild(_diagnosticsGpuGraph);
        var sort = new HBoxContainer(); sort.AddChild(new Label { Text = "Sort rows", CustomMinimumSize = new Vector2(100, 0) }); _diagnosticsSort = new OptionButton { Name = "DiagnosticsSort", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Disabled = !available }; foreach (string name in new[] { "Average", "P95", "Worst", "Name" }) _diagnosticsSort.AddItem(name); _diagnosticsSort.Select(2); _diagnosticsSort.ItemSelected += _ => RefreshDiagnostics(); sort.AddChild(_diagnosticsSort); results.AddChild(sort);
        _diagnosticsRows = new Label { Name = "DiagnosticsRows", AutowrapMode = TextServer.AutowrapMode.WordSmart }; results.AddChild(_diagnosticsRows);
        VBoxContainer memory = TuningMenuUi.CreateSection(tab, "Memory and workload");
        _diagnosticsMemory = new Label { Name = "DiagnosticsMemory", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        memory.AddChild(_diagnosticsMemory);
        VBoxContainer actions = TuningMenuUi.CreateSection(tab, "Capture and settings");
        Button button = new() { Name = "DiagnosticsCaptureSlowdown", Text = "Capture slowdown report", Disabled = !available }; button.Pressed += () => { if (_diagnosticsStatus != null) _diagnosticsStatus.Text = _performanceMonitor?.CaptureSlowdown() ?? "Monitor unavailable."; }; actions.AddChild(button);
        button = new Button { Name = "DiagnosticsClearHistory", Text = "Clear history", Disabled = !available }; button.Pressed += () => { _performanceMonitor?.ClearHistory(); RefreshDiagnostics(); }; actions.AddChild(button);
        button = new Button { Name = "DiagnosticsSaveSettings", Text = "Save diagnostics settings", Disabled = !available }; button.Pressed += () => { if (_diagnosticsStatus != null) _diagnosticsStatus.Text = _performanceMonitor?.SaveSettings() == true ? "Settings saved." : "Settings could not be saved."; }; actions.AddChild(button);
        button = new Button { Name = "DiagnosticsResetSettings", Text = "Reset diagnostics settings", Disabled = !available }; button.Pressed += () => { _performanceMonitor?.ResetSettings(); SyncDiagnosticsControls(); RefreshDiagnostics(); }; actions.AddChild(button);
        _diagnosticsStatus = new Label { Name = "DiagnosticsStatus", Text = available ? $"Settings: {RingPerformanceMonitor.UserPath}" : "Performance monitor unavailable in this scene.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted }; actions.AddChild(_diagnosticsStatus);
    }

    private void ProcessDiagnosticsTuning(double delta) { if (!_open || _diagnosticsSummary == null) return; _diagnosticsRefreshElapsed += delta; if (_diagnosticsRefreshElapsed < Math.Clamp(_performanceMonitor?.RefreshSeconds ?? .2f, .1f, 1f)) return; _diagnosticsRefreshElapsed = 0; RefreshDiagnostics(); }
    public void RefreshDiagnostics()
    {
        using var observer = RingPerformanceTelemetry.MeasureCpu("Diagnostics UI refresh");
        if (_diagnosticsSummary == null || _diagnosticsRows == null) return; RingPerformanceSnapshot? s = _performanceMonitor?.GetSnapshot(); if (s == null) { _diagnosticsSummary.Text = "Performance monitor unavailable."; return; }
        RingPerformanceFrame[] frames = s.History.TakeLast(300).ToArray(); if (frames.Length == 0) { _diagnosticsSummary.Text = "Collecting frame history..."; _diagnosticsRows.Text = string.Empty; return; }
        double[] samples = frames.Select(f => f.FrameMilliseconds).OrderBy(v => v).ToArray(); int p95 = Math.Clamp((int)Math.Ceiling(samples.Length * .95) - 1, 0, samples.Length - 1);
        double window = frames[^1].Seconds - frames[0].Seconds;
        _diagnosticsSummary.Text = $"{(_performanceMonitor?.Enabled == true ? "Collecting" : "Recording paused; history frozen")} | FPS {Engine.GetFramesPerSecond():0} | frame avg {frames.Average(f => f.FrameMilliseconds):0.00} ms | p95 {samples[p95]:0.00} ms | worst {samples[^1]:0.00} ms (last {window:0.0}s)\nMain viewport CPU/GPU render interval: {(frames[^1].ViewportCpuMilliseconds?.ToString("0.00") ?? "n/a")} / {(frames[^1].ViewportGpuMilliseconds?.ToString("0.00") ?? "n/a")} ms. Far rendering is separate/uninstrumented. GPU detail: {RingPerformanceGpu.Status}\nPreset {s.Context.GraphicsPreset}, render {s.Context.RenderScale * 100f:0}% | speed {s.Context.SpeedMetersPerSecond:0.0} m/s | draws {s.Context.DrawCalls}.";
        if (_diagnosticsMemory != null) _diagnosticsMemory.Text = $"Working {Bytes(s.WorkingSetBytes)} | managed {Bytes(s.ManagedHeapBytes)} | allocated {Bytes(s.AllocatedBytes)} | video {Bytes(s.Context.ReportedVideoMemoryBytes)}\nTelemetry retained {Bytes(s.RetainedTelemetryBytes)} / {_performanceMonitor?.MemoryBudgetMiB ?? 0} MiB budget | effective history {s.EffectiveHistorySeconds:0.0}s | dropped {s.DroppedSamples}\nGC {s.Gen0Collections}/{s.Gen1Collections}/{s.Gen2Collections}\nTerrain {s.Context.TerrainCoordinator}; grid {s.Context.ChunkGrid}; smooth {s.Context.SmoothTerrain}; trees {s.Context.Trees}; grass {s.Context.Grass}; water {s.Context.Water}; shadow atlas {s.Context.ShadowAtlas}.";
        _diagnosticsCpuGraph?.SetFrames(frames); _diagnosticsGpuGraph?.SetFrames(frames); var rows = _diagnosticsSort?.Selected switch { 0 => s.Rows.OrderByDescending(r => r.MeanMilliseconds), 1 => s.Rows.OrderByDescending(r => r.P95Milliseconds), 3 => s.Rows.OrderBy(r => r.Name), _ => s.Rows.OrderByDescending(r => r.MaxMilliseconds) }; var text = new StringBuilder("Name | avg | p95 | worst | count | duration | freshness\n"); foreach (RingPerformanceRow row in rows!.Take(18)) { double age = Math.Max(0, RingPerformanceMonitor.NowSeconds - row.LastSeenSeconds); text.Append($"{row.Name} | {row.MeanMilliseconds:0.00} | {row.P95Milliseconds:0.00} | {row.MaxMilliseconds:0.00} ms | {row.Count} | {(row.Gpu ? "GPU interval" : row.Worker ? "worker job" : "main CPU call")} | {age:0.0}s ago, src {row.SourceFrameId}\n"); } text.Append(s.Caveat); _diagnosticsRows.Text = text.ToString();
    }
    private static RingPerformanceGraph Graph(string name, RingPerformanceGraphMetric metric) => new() { Name = name, Metric = metric, CustomMinimumSize = new Vector2(0, 86), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private static string Bytes(long b) => b < 0 ? "unavailable" : b >= 1048576 ? $"{b / 1048576.0:0.0} MiB" : $"{b / 1024.0:0.0} KiB";
    private void SyncDiagnosticsControls() { if (_performanceMonitor == null) return; _diagnosticsMonitoringToggle?.SetPressedNoSignal(_performanceMonitor.Enabled); _diagnosticsGpuToggle?.SetPressedNoSignal(_performanceMonitor.DetailedGpuEnabled); _diagnosticsOverlayToggle?.SetPressedNoSignal(_performanceHud?.Visible ?? false); SyncSlider(_diagnosticsHistorySlider, _performanceMonitor.HistorySeconds); SyncSlider(_diagnosticsMemorySlider, _performanceMonitor.MemoryBudgetMiB); SyncSlider(_diagnosticsRefreshSlider, _performanceMonitor.RefreshSeconds); }
    private void SyncSlider(HSlider? slider, double value) { if (slider == null) return; slider.SetValueNoSignal(value); if (_diagnosticsSliderValues.TryGetValue(slider, out Label? label)) label.Text = value.ToString("0.###"); }
    private static CheckButton Toggle(VBoxContainer p, string name, string text, bool initial, Action<bool> apply, bool enabled) { var c = new CheckButton { Name = name, Text = text, ButtonPressed = initial, Disabled = !enabled }; c.Toggled += value => apply(value); p.AddChild(c); return c; }
    private HSlider Slider(VBoxContainer p, string name, string text, double min, double max, double step, double initial, Action<double> apply, bool enabled) { p.AddChild(new Label { Text = text }); var row = new HBoxContainer(); var c = new HSlider { Name = name, MinValue = min, MaxValue = max, Step = step, Value = initial, Editable = enabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; var value = new Label { Text = initial.ToString("0.###"), CustomMinimumSize = new Vector2(54, 0) }; _diagnosticsSliderValues[c] = value; c.ValueChanged += v => { value.Text = v.ToString("0.###"); apply(v); }; row.AddChild(c); row.AddChild(value); p.AddChild(row); return c; }
}
