using System;
using System.Threading;

namespace RingworldTechDemo.Diagnostics;

/// <summary>Opt-in, bounded render-thread CPU samples. Formatting and I/O belong to the capture driver.</summary>
internal static class RingVegetationWorkTrace
{
    internal struct Sample
    {
        public ulong RequestFrame, RequestUsec, StartUsec, EndUsec;
        public int Instance, Channel, CallbackDepth, NewlyAllocated, RetiredBatches, RetiredMirrors;
        public long AllocatedBytes, EnsureTicks, RetireTicks, TotalTicks;
        public bool Failed;
    }

    internal sealed class Capture
    {
        internal readonly Sample[] Samples;
        private int _count;
        private long _overflow;
        private int _complete;
        private readonly object _gate = new();
        private bool _closed;
        private int _pending;
        private int _renderDepth;
        internal int Count => Volatile.Read(ref _count);
        internal long Overflow => Interlocked.Read(ref _overflow);
        internal bool Complete => Volatile.Read(ref _complete) != 0;
        internal Capture(int capacity) => Samples = new Sample[capacity];
        internal int EnterCallback() => ++_renderDepth;
        internal void LeaveCallback() => _renderDepth--;

        // Godot's render thread is the single writer; published rows are immutable.
        internal void Record(in Sample sample)
        {
            int index = _count;
            if (index == Samples.Length) { Interlocked.Increment(ref _overflow); return; }
            Samples[index] = sample;
            Volatile.Write(ref _count, index + 1);
        }

        internal bool TryRetain()
        {
            lock (_gate)
            {
                if (_closed) return false;
                _pending++;
                return true;
            }
        }

        internal void Release()
        {
            lock (_gate)
            {
                _pending--;
                if (_closed && _pending == 0) Volatile.Write(ref _complete, 1);
            }
        }

        internal void Close()
        {
            lock (_gate)
            {
                _closed = true;
                if (_pending == 0) Volatile.Write(ref _complete, 1);
            }
        }
    }

    private static Capture? _current;
    internal static Capture? Current => Volatile.Read(ref _current);

    internal static Capture Begin(int capacity)
    {
        if (capacity < 1 || capacity > 262144) throw new ArgumentOutOfRangeException(nameof(capacity));
        var capture = new Capture(capacity);
        if (Interlocked.CompareExchange(ref _current, capture, null) != null)
            throw new InvalidOperationException("A vegetation render trace is already active.");
        return capture;
    }

    // Close first: a caller that read Current before Stop must still fail admission after Close.
    internal static void Stop(Capture capture)
    {
        capture.Close();
        Interlocked.CompareExchange(ref _current, null, capture);
    }
}
