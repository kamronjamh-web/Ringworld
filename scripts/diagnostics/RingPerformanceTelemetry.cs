using System;
using System.Diagnostics;
using System.Threading;

namespace RingworldTechDemo.Diagnostics;

public static class RingPerformanceTelemetry
{
    internal readonly record struct Completed(string Name, bool Worker, double Milliseconds, bool Gpu, long SourceFrameId, double CompletedSeconds);
    private const int MaximumNames = 128;
    private static readonly object Gate = new();
    private static Completed[] _pending = new Completed[1024];
    private static string[] _names = new string[MaximumNames];
    private static int _nameCount, _head, _count, _generation, _enabled, _dropped;
    public static void Configure(bool enabled, int pendingCapacity = 1024)
    {
        lock (Gate) { bool was = _enabled != 0; if (enabled && !was) { _generation++; _enabled = 1; ResizeLocked(Math.Clamp(pendingCapacity, 64, 16384)); } else if (enabled) { ResizeLocked(Math.Clamp(pendingCapacity, 64, 16384)); } else if (was) { _generation++; _enabled = 0; _head = _count = _nameCount = _dropped = 0; } }
    }
    public static CpuScope MeasureCpu(string name, bool worker = false)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128) return default;
        if (Volatile.Read(ref _enabled) == 0) return default;
        lock (Gate) { if (_enabled == 0 || !AcceptNameLocked(name)) return default; return new CpuScope(name, worker, Stopwatch.GetTimestamp(), _generation); }
    }
    public static void RecordGpuMilliseconds(string name, double milliseconds, long sourceFrameId = -1)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || !double.IsFinite(milliseconds) || milliseconds < 0) return;
        lock (Gate) { if (_enabled == 0 || !AcceptNameLocked(name)) return; EnqueueLocked(new Completed(name, false, milliseconds, true, sourceFrameId, NowSeconds())); }
    }
    internal static int Drain(Span<Completed> destination, out int dropped)
    {
        lock (Gate) { int written = Math.Min(destination.Length, _count); for (int i = 0; i < written; i++) destination[i] = _pending[(_head + i) % _pending.Length]; _head = (_head + written) % _pending.Length; _count -= written; dropped = _dropped; _dropped = 0; return written; }
    }
    public static void Reset() => Configure(false);
    public static double NowSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private static bool AcceptNameLocked(string name) { for (int i = 0; i < _nameCount; i++) if (string.Equals(_names[i], name, StringComparison.Ordinal)) return true; if (_nameCount >= MaximumNames) return false; _names[_nameCount++] = name; return true; }
    private static void EnqueueLocked(Completed sample) { if (_count == _pending.Length) { _head = (_head + 1) % _pending.Length; _count--; _dropped++; } _pending[(_head + _count) % _pending.Length] = sample; _count++; }
    private static void ResizeLocked(int capacity) { if (_pending.Length == capacity) return; var next = new Completed[capacity]; int copied = Math.Min(_count, capacity); for (int i = 0; i < copied; i++) next[i] = _pending[(_head + _count - copied + i) % _pending.Length]; _dropped += _count - copied; _pending = next; _head = 0; _count = copied; }
    public readonly struct CpuScope : IDisposable
    {
        private readonly string? _name; private readonly bool _worker; private readonly long _start; private readonly int _generation;
        internal CpuScope(string name, bool worker, long start, int generation) { _name = name; _worker = worker; _start = start; _generation = generation; }
        public void Dispose() { if (_name == null) return; double ms = (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency; if (!double.IsFinite(ms) || ms < 0) return; lock (Gate) { if (_enabled == 0 || _generation != RingPerformanceTelemetry._generation) return; EnqueueLocked(new Completed(_name, _worker, ms, false, -1, NowSeconds())); } }
    }
}
