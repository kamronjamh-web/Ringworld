using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Diagnostics;

public partial class RingPerformanceMonitor
{
    private const int LiveCpuLimit = 4096, LiveGpuLimit = 1024, LiveFrameLimit = 300;
    // Includes reusable scratch, the cached display snapshot, and row storage.
    private const long LiveWorkspaceReserve = 512L * 1024L;
    private readonly RingPerformanceTelemetry.Completed[] _liveSamples = new RingPerformanceTelemetry.Completed[LiveCpuLimit + LiveGpuLimit];
    private RingPerformanceSnapshot? _liveSnapshot;
    private double _nextLiveSummary;
    internal int LiveSummaryBuildCount { get; private set; }
    internal int LastLiveSummarySampleCount { get; private set; }
    private static readonly IComparer<RingPerformanceTelemetry.Completed> LiveSampleOrder =
        Comparer<RingPerformanceTelemetry.Completed>.Create(static (a, b) =>
        {
            int result = string.CompareOrdinal(a.Name, b.Name);
            if (result == 0) result = a.Worker.CompareTo(b.Worker);
            if (result == 0) result = a.Gpu.CompareTo(b.Gpu);
            return result != 0 ? result : a.Milliseconds.CompareTo(b.Milliseconds);
        });

    /// <summary>Bounded recent statistics for displays. Full capture statistics remain in GetSnapshot.</summary>
    public RingPerformanceSnapshot GetLiveSnapshot()
    {
        double now = RingPerformanceTelemetry.NowSeconds();
        if (_liveSnapshot != null && now < _nextLiveSummary) return _liveSnapshot;
        using var scope = RingPerformanceTelemetry.MeasureCpu("Diagnostics.LiveSummary");
        _nextLiveSummary = now + Math.Max(.5, RefreshSeconds);
        int count = 0;
        double cutoff = _clock - Math.Min(5, HistorySeconds);
        CopyRecent(_events, _eventHead, _eventCount, LiveCpuLimit);
        CopyRecent(_gpuEvents, _gpuEventHead, _gpuEventCount, LiveGpuLimit);
        LastLiveSummarySampleCount = count;
        LiveSummaryBuildCount++;
        Array.Sort(_liveSamples, 0, count, LiveSampleOrder);
        var rows = new List<RingPerformanceRow>();
        for (int first = 0; first < count;)
        {
            var key = _liveSamples[first];
            var latest = key;
            int end = first;
            double sum = 0;
            while (end < count && _liveSamples[end].Name == key.Name
                && _liveSamples[end].Worker == key.Worker && _liveSamples[end].Gpu == key.Gpu)
            {
                var sample = _liveSamples[end++];
                sum += sample.Milliseconds;
                if (sample.CompletedSeconds > latest.CompletedSeconds) latest = sample;
            }
            int size = end - first;
            int p95 = first + (int)Math.Ceiling(size * .95) - 1;
            rows.Add(new(key.Name, key.Worker, key.Gpu, size, sum / size,
                _liveSamples[p95].Milliseconds, _liveSamples[end - 1].Milliseconds,
                latest.CompletedSeconds, latest.SourceFrameId));
            first = end;
        }
        int frames = Math.Min(_frameCount, LiveFrameLimit);
        var history = new RingPerformanceFrame[frames];
        for (int i = 0; i < frames; i++) history[i] = _frames[(_frameHead + _frameCount - frames + i) % _frames.Length];
        double effective = _frameCount > 1
            ? _frames[(_frameHead + _frameCount - 1) % _frames.Length].Seconds - _frames[_frameHead].Seconds : 0;
        return _liveSnapshot = new(rows, history, _context, _workingSet, _heap, _allocated, _g0, _g1, _g2,
            _testGpuAvailability ?? _gpuAvailable,
            "Live scope rows: last 5 seconds, capped at 4096 CPU / 1024 GPU samples; refreshed at most twice per second. Full history statistics are in the slowdown export.",
            _dropped + _gpuDropped, effective, EstimatedRetainedBytes());

        void CopyRecent(RingPerformanceTelemetry.Completed[] source, int head, int retained, int limit)
        {
            for (int i = Math.Max(0, retained - limit); i < retained; i++)
            {
                var sample = source[(head + i) % source.Length];
                if (sample.CompletedSeconds >= cutoff) _liveSamples[count++] = sample;
            }
        }
    }

    private long EstimatedRetainedBytes() => (long)_frames.Length * 224L
        + ((long)_events.Length + _gpuEvents.Length + _drain.Length + _gpuDrain.Length
            + RingPerformanceTelemetry.PendingCpuCapacity + RingPerformanceTelemetry.PendingGpuCapacity) * 64L
        + CloudHistoryEstimatedBytes + 32768 + LiveWorkspaceReserve;
}
