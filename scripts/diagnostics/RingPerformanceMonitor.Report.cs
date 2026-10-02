using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Diagnostics;

/// <summary>Capture-only v2 report analysis. GPU timestamps are completion-time data,
/// deliberately separate from equal-wall-time CPU/frame bins.</summary>
public partial class RingPerformanceMonitor
{
    private object CaptureMetadata() => new
    {
        utc = DateTime.UtcNow.ToString("O"), engine = EngineVersion(), renderer = RenderingServer.GetCurrentRenderingMethod(),
        gpuAdapter = RenderingServer.GetVideoAdapterName(), monitoring = new { Enabled, DetailedGpuEnabled, HistorySeconds, MemoryBudgetMiB, RefreshSeconds },
        retention = new { cpuPending = RingPerformanceTelemetry.PendingCpuCount, cpuPendingCapacity = RingPerformanceTelemetry.PendingCpuCapacity, gpuPending = RingPerformanceTelemetry.PendingGpuCount, gpuPendingCapacity = RingPerformanceTelemetry.PendingGpuCapacity, cpuRetention = _eventCount, cpuRetentionCapacity = _events.Length, gpuRetention = _gpuEventCount, gpuRetentionCapacity = _gpuEvents.Length, cpuHistoryDropped = _dropped, gpuHistoryDropped = _gpuDropped, cpuRejectedNames = RingPerformanceTelemetry.RejectedCpuNames, gpuRejectedNames = RingPerformanceTelemetry.RejectedGpuNames },
        gpuTimestampStatus = RingPerformanceGpu.Status, gpuTimestamp = RingPerformanceGpu.GetDiagnostics(),
        coverage = new
        {
            frameOldestSeconds = _frameCount == 0 ? (double?)null : _frames[_frameHead].Seconds,
            frameNewestSeconds = _frameCount == 0 ? (double?)null : _frames[(_frameHead + _frameCount - 1) % _frames.Length].Seconds,
            cpuOldestCompletionSeconds = _eventCount == 0 ? (double?)null : _events[_eventHead].CompletedSeconds,
            cpuNewestCompletionSeconds = _eventCount == 0 ? (double?)null : _events[(_eventHead + _eventCount - 1) % _events.Length].CompletedSeconds,
            gpuOldestCompletionSeconds = _gpuEventCount == 0 ? (double?)null : _gpuEvents[_gpuEventHead].CompletedSeconds,
            gpuNewestCompletionSeconds = _gpuEventCount == 0 ? (double?)null : _gpuEvents[(_gpuEventHead + _gpuEventCount - 1) % _gpuEvents.Length].CompletedSeconds
        },
        counterNotes = "Dropped counts combine bounded pending/retention overwrites; CPU also includes frame-buffer overwrites. Normal history expiry is excluded. GPU diagnostic counters are process-lifetime cumulative; UnmatchedReadbacks counts fresh readbacks with no complete owned scope. MarkerBudgetPerFrame applies to C# markers, excluding the bounded Sunshine scopes.",
        memoryNote = "RetainedTelemetryBytes is a conservative storage estimate including pending queues, retained buffers and reserved cloud sample slots. Summary/export allocations are transient and excluded. AllocatedBytes is process-lifetime cumulative.",
        caveats = "One-second bins are equal wall-time windows for frame/main viewport data. GPU scope samples are delayed completion-time timestamps and must not be paired with CPU frames or summed across nested categories."
    };

    private object[] CaptureBins()
    {
        var bins = new SortedDictionary<long, List<RingPerformanceFrame>>();
        for (int i = 0; i < _frameCount; i++)
        {
            RingPerformanceFrame frame = _frames[(_frameHead + i) % _frames.Length]; long second = (long)Math.Floor(frame.Seconds);
            if (!bins.TryGetValue(second, out List<RingPerformanceFrame>? list)) bins[second] = list = new(); list.Add(frame);
        }
        var result = new List<object>(bins.Count);
        foreach ((long second, List<RingPerformanceFrame> frames) in bins)
            result.Add(new { startSeconds = second, endSeconds = second + 1, frameMilliseconds = Stats(frames, f => f.FrameMilliseconds), mainViewportCpuMilliseconds = Stats(frames, f => f.ViewportCpuMilliseconds), mainViewportGpuMilliseconds = Stats(frames, f => f.ViewportGpuMilliseconds), frameCount = frames.Count, gpuCompletionCategories = GpuCompletionBin(second), timestampQuality = "wall-time frame samples; GPU viewport values are renderer-delayed and not CPU-frame paired. Detailed GPU categories use completion-time seconds." });
        return result.ToArray();
    }

    private object[] CaptureGpuSourceGroups()
    {
        var groups = new SortedDictionary<long, List<RingPerformanceTelemetry.Completed>>();
        for (int i = 0; i < _gpuEventCount; i++)
        {
            RingPerformanceTelemetry.Completed sample = _gpuEvents[(_gpuEventHead + i) % _gpuEvents.Length];
            if (!groups.TryGetValue(sample.SourceFrameId, out List<RingPerformanceTelemetry.Completed>? list)) groups[sample.SourceFrameId] = list = new(); list.Add(sample);
        }
        var output = new List<object>(groups.Count);
        foreach ((long frame, List<RingPerformanceTelemetry.Completed> samples) in groups)
        {
            var categories = new Dictionary<string, List<RingPerformanceTelemetry.Completed>>(StringComparer.Ordinal);
            foreach (RingPerformanceTelemetry.Completed sample in samples) { if (!categories.TryGetValue(sample.Name, out List<RingPerformanceTelemetry.Completed>? list)) categories[sample.Name] = list = new(); list.Add(sample); }
            var detail = new List<object>(categories.Count);
            foreach ((string category, List<RingPerformanceTelemetry.Completed> values) in categories) detail.Add(new { category, milliseconds = Stats(values, v => v.Milliseconds) });
            output.Add(new { sourceFrameId = frame, categories = detail, timestampQuality = "delayed GPU completion-time samples; category values are intentionally not summed because scopes can overlap" });
        }
        return output.ToArray();
    }

    private object[] GpuCompletionBin(long second)
    {
        var categories = new Dictionary<string, List<RingPerformanceTelemetry.Completed>>(StringComparer.Ordinal);
        for (int i = 0; i < _gpuEventCount; i++)
        {
            RingPerformanceTelemetry.Completed sample = _gpuEvents[(_gpuEventHead + i) % _gpuEvents.Length];
            if ((long)Math.Floor(sample.CompletedSeconds) != second) continue;
            if (!categories.TryGetValue(sample.Name, out List<RingPerformanceTelemetry.Completed>? values)) categories[sample.Name] = values = new(); values.Add(sample);
        }
        var output = new List<object>(categories.Count);
        foreach ((string category, List<RingPerformanceTelemetry.Completed> values) in categories)
        {
            var sourceFrames = new HashSet<long>(); double first = double.PositiveInfinity, last = double.NegativeInfinity;
            foreach (RingPerformanceTelemetry.Completed value in values) { sourceFrames.Add(value.SourceFrameId); first = Math.Min(first, value.CompletedSeconds); last = Math.Max(last, value.CompletedSeconds); }
            output.Add(new { category, milliseconds = Stats(values, v => v.Milliseconds), sampleCount = values.Count, distinctGpuSourceFrameCount = sourceFrames.Count, firstCompletionSeconds = first, lastCompletionSeconds = last, timestampQuality = "GPU completion-time bin; not frame paired and not additive across overlapping scopes" });
        }
        return output.ToArray();
    }

    private static object EngineVersion()
    {
        Godot.Collections.Dictionary source = Engine.GetVersionInfo();
        string Text(string key) => source.TryGetValue(key, out Variant value) ? value.AsString() : "";
        long Number(string key) => source.TryGetValue(key, out Variant value) && value.VariantType is Variant.Type.Int or Variant.Type.Float ? value.AsInt64() : 0;
        return new { major = Number("major"), minor = Number("minor"), patch = Number("patch"), status = Text("status"), build = Text("build"), hash = Text("hash"), @string = Text("string") };
    }

    private static object? Stats<T>(IReadOnlyList<T> values, Func<T, double?> value)
    {
        var numbers = new List<double>(values.Count);
        for (int i = 0; i < values.Count; i++) if (value(values[i]) is double number && double.IsFinite(number)) numbers.Add(number);
        if (numbers.Count == 0) return null;
        numbers.Sort(); double sum = 0; foreach (double number in numbers) sum += number;
        double At(double percentile) => numbers[(int)Math.Clamp(Math.Ceiling(numbers.Count * percentile) - 1, 0, numbers.Count - 1)];
        return new { sampleCount = numbers.Count, mean = sum / numbers.Count, p50 = At(.50), p95 = At(.95), p99 = At(.99), max = numbers[^1] };
    }
}
