using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Generation;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>What the last real bake or file load did, for the benchmark and the loading report (seconds are wall clock).</summary>
public sealed record HydrologyBakeStats
{
    public string Source { get; init; } = "";
    /// <summary>True when the graph came from a valid file and nothing was baked.</summary>
    public bool Loaded { get; init; }
    public double GridSeconds { get; init; }
    public double BarrierSeconds { get; init; }
    public double NoiseSeconds { get; init; }
    public double ConditioningSeconds { get; init; }
    public double RoutingSeconds { get; init; }
    public double GraphSeconds { get; init; }
    public double PackSeconds { get; init; }
    public double WriteSeconds { get; init; }
    public double LoadSeconds { get; init; }
    public double TotalSeconds { get; init; }
    public long FileBytes { get; init; }
    public int Workers { get; init; }
}

/// <summary>
/// The resident river graphs of this process, one per pre-graph identity (S12, D14), backed by the graph file of the world. A world with rivers gets its graph once: a valid file is read and never replaced;
/// a missing or invalid one is baked (GPU source with the CPU source as fallback), written atomically and kept. Concurrent callers of one identity share one build; a cancelled caller leaves it, and the
/// last one out cancels it (the temp file is deleted, no file is published).
/// </summary>
public static class RiverGraphStore
{
    private sealed class Entry
    {
        public Task<RiverGraph> Task = null!;
        public CancellationTokenSource Cancel = new();
        public int Waiters;
        public readonly List<IProgress<double>> Listeners = new();
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, RiverGraph> Done = new();
    private static readonly Dictionary<ulong, Entry> InFlight = new();
    private static int _bakes, _loads;

    /// <summary>Real bakes finished since the last <see cref="Clear"/>.</summary>
    public static int BakeCount => Volatile.Read(ref _bakes);
    /// <summary>File loads finished since the last <see cref="Clear"/>.</summary>
    public static int LoadCount => Volatile.Read(ref _loads);
    public static HydrologyBakeStats? LastStats { get; private set; }

    /// <summary>Test hook: replaces the bake (a strip graph for the store tests). Receives the inputs, the identity and a progress sink; returns the graph and its source byte.</summary>
    internal static Func<HydrologyBakeInputs, ulong, CancellationToken, IProgress<double>?, (RiverGraph Graph, byte Source)>? BakeOverride { get; set; }

    public static bool TryGet(ulong identity, out RiverGraph? graph)
    {
        lock (Gate) return Done.TryGetValue(identity, out graph);
    }

    /// <summary>Forgets every resident graph and cancels nothing in flight; for tests and the benchmark.</summary>
    public static void Clear()
    {
        lock (Gate) { Done.Clear(); _bakes = 0; _loads = 0; LastStats = null; }
    }

    /// <summary>Makes <paramref name="graph"/> the resident graph of <paramref name="identity"/> (a strip graph in tests).</summary>
    public static void InstallForTest(ulong identity, RiverGraph graph)
    {
        lock (Gate) Done[identity] = graph;
    }

    /// <summary>Synchronous form for tools, tests and the diagnostics launch.</summary>
    public static RiverGraph GetOrBuild(HydrologyBakeInputs inputs, CancellationToken cancellation = default, IProgress<double>? progress = null)
        => GetOrBuildAsync(inputs, cancellation, progress).GetAwaiter().GetResult();

    public static Task<RiverGraph> GetOrBuildAsync(HydrologyBakeInputs inputs, CancellationToken cancellation = default, IProgress<double>? progress = null)
    {
        ulong identity = inputs.Identity;
        Entry entry;
        lock (Gate)
        {
            if (Done.TryGetValue(identity, out RiverGraph? ready)) { progress?.Report(1d); return Task.FromResult(ready); }
            if (!InFlight.TryGetValue(identity, out entry!))
            {
                entry = new Entry();
                InFlight[identity] = entry;
                Entry created = entry;
                if (progress != null) created.Listeners.Add(progress);
                created.Waiters = 1;
                created.Task = Task.Run(() => Produce(inputs, identity, created), CancellationToken.None);
            }
            else
            {
                entry.Waiters++;
                if (progress != null) entry.Listeners.Add(progress);
            }
        }
        return Wait(entry, identity, cancellation);
    }

    private static async Task<RiverGraph> Wait(Entry entry, ulong identity, CancellationToken cancellation)
    {
        using CancellationTokenRegistration registration = cancellation.Register(() => Leave(entry, identity));
        return await entry.Task.WaitAsync(cancellation).ConfigureAwait(false);
    }

    private static void Leave(Entry entry, ulong identity)
    {
        bool cancel;
        lock (Gate)
        {
            if (!InFlight.TryGetValue(identity, out Entry? current) || current != entry) return;
            cancel = --entry.Waiters <= 0;
        }
        if (!cancel) return;
        try { entry.Cancel.Cancel(); }
        catch (ObjectDisposedException) { /* the build finished first */ }
    }

    private static RiverGraph Produce(HydrologyBakeInputs inputs, ulong identity, Entry entry)
    {
        CancellationToken ct = entry.Cancel.Token;
        var progress = new Fan(entry);
        try
        {
            var total = Stopwatch.StartNew();
            HydrologyBakeStats? loadStats = null;
            RiverGraph? graph = inputs.Directory.Length > 0 ? TryLoad(inputs, identity, out loadStats, total) : null;
            if (graph != null)
            {
                Interlocked.Increment(ref _loads);
                LastStats = loadStats;
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                graph = Bake(inputs, identity, ct, progress, total);
                Interlocked.Increment(ref _bakes);
            }
            progress.Report(1d);
            lock (Gate) Done[identity] = graph;
            return graph;
        }
        finally
        {
            lock (Gate) { if (InFlight.TryGetValue(identity, out Entry? current) && current == entry) InFlight.Remove(identity); }
            entry.Cancel.Dispose();
        }
    }

    private sealed class Fan : IProgress<double>
    {
        private readonly Entry _entry;
        public Fan(Entry entry) => _entry = entry;
        public void Report(double value)
        {
            IProgress<double>[] listeners;
            lock (Gate) listeners = _entry.Listeners.ToArray();
            foreach (IProgress<double> listener in listeners) listener.Report(value);
        }
    }

    private static RiverGraph? TryLoad(HydrologyBakeInputs inputs, ulong identity, out HydrologyBakeStats? stats, Stopwatch total)
    {
        stats = null;
        string path = inputs.FilePath(identity);
        if (!File.Exists(path)) return null;
        var clock = Stopwatch.StartNew();
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (!RiverGraphFormat.TryRead(bytes, identity, out RiverGraph? graph, out _) || graph == null) return null;
            stats = new HydrologyBakeStats { Loaded = true, Source = graph.Source == 1 ? "gpu" : "cpu", LoadSeconds = clock.Elapsed.TotalSeconds, TotalSeconds = total.Elapsed.TotalSeconds, FileBytes = bytes.Length, Workers = inputs.Workers };
            return graph;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static RiverGraph Bake(HydrologyBakeInputs inputs, ulong identity, CancellationToken ct, IProgress<double> progress, Stopwatch total)
    {
        (RiverGraph graph, byte source, HydrologyBakeStats partial) = BakeOverride != null
            ? Wrap(BakeOverride(inputs, identity, ct, progress))
            : BakeRing(inputs, identity, ct, progress);
        double writeSeconds = 0d;
        long bytes = 0;
        if (inputs.Directory.Length > 0)
        {
            ct.ThrowIfCancellationRequested();
            var clock = Stopwatch.StartNew();
            bytes = Write(inputs.FilePath(identity), RiverGraphFormat.Write(graph), ct);
            writeSeconds = clock.Elapsed.TotalSeconds;
        }
        LastStats = partial with { WriteSeconds = writeSeconds, FileBytes = bytes, TotalSeconds = total.Elapsed.TotalSeconds, Workers = inputs.Workers, Source = source == 1 ? "gpu" : "cpu" };
        return graph;
    }

    private static (RiverGraph, byte, HydrologyBakeStats) Wrap((RiverGraph Graph, byte Source) built) => (built.Graph, built.Source, new HydrologyBakeStats());

    /// <summary>Same-directory temp file, published with File.Move; cancellation or a failure deletes the temp and publishes nothing.</summary>
    private static long Write(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, bytes);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
            return bytes.Length;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    // Progress weights (S12): grid .25, barriers .05, conditioning .30, routing .20, graph .17, pack .03. The drainage bake reports 0.1 after the barriers and noise, 0.6 after conditioning and 0.9 after routing.
    private static (RiverGraph, byte, HydrologyBakeStats) BakeRing(HydrologyBakeInputs inputs, ulong identity, CancellationToken ct, IProgress<double> progress)
    {
        TerrainGenerationParameters p = inputs.Parameters;
        DrainageGridSpec spec = DrainageGridSpec.Ring(p);
        RenderingDevice? device = null;
        IDrainageHeightSource? source = null;
        try
        {
            if (inputs.Source != HydrologySourcePreference.Cpu && inputs.GpuAvailable)
            {
                try
                {
                    device = RenderingServer.CreateLocalRenderingDevice();
                    if (device != null) source = new GpuDrainageHeightSource(device, p, inputs.Climate);
                }
                catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
                {
                    GD.PushWarning("River bake: GPU source unavailable, using the CPU source. " + e.Message);
                    (source as IDisposable)?.Dispose();
                    source = null;
                }
                if (source == null) { device?.Free(); device = null; }
            }
            if (source == null && inputs.Source == HydrologySourcePreference.Gpu) throw new InvalidOperationException("The GPU source was requested but is not available.");
            source ??= new CpuDrainageHeightSource(p, inputs.Climate, inputs.Workers);

            var clock = Stopwatch.StartNew();
            DrainageGrid grid = source.SampleGrid(spec, new Scaled(progress, 0d, 0.25d), ct);
            double gridSeconds = clock.Elapsed.TotalSeconds;
            DrainageField field = DrainageBake.Bake(grid, p, inputs.Workers, new BakeScale(progress), ct);
            clock.Restart();
            RiverNetwork net = RiverGraphBuilder.Build(field, grid, p, new RiverBuildOptions { Source = source, Workers = inputs.Workers }, ct, out _);
            double graphSeconds = clock.Elapsed.TotalSeconds;
            progress.Report(0.97d);
            clock.Restart();
            byte sourceByte = source.Name == "gpu" ? (byte)1 : (byte)0;
            RiverGraph graph = RiverGraph.Build(net, field, grid, identity, sourceByte);
            var stats = new HydrologyBakeStats
            {
                GridSeconds = gridSeconds, BarrierSeconds = field.Report.BarrierSeconds, NoiseSeconds = field.Report.NoiseSeconds, ConditioningSeconds = field.Report.ConditioningSeconds,
                RoutingSeconds = field.Report.RoutingSeconds, GraphSeconds = graphSeconds, PackSeconds = clock.Elapsed.TotalSeconds,
            };
            return (graph, sourceByte, stats);
        }
        finally
        {
            (source as IDisposable)?.Dispose();
            device?.Free();
        }
    }

    private sealed class Scaled : IProgress<double>
    {
        private readonly IProgress<double> _inner;
        private readonly double _from, _span;
        public Scaled(IProgress<double> inner, double from, double to) { _inner = inner; _from = from; _span = to - from; }
        public void Report(double value) => _inner.Report(_from + Math.Clamp(value, 0d, 1d) * _span);
    }

    private sealed class BakeScale : IProgress<double>
    {
        private readonly IProgress<double> _inner;
        public BakeScale(IProgress<double> inner) => _inner = inner;
        public void Report(double value) => _inner.Report(value <= 0.1d ? 0.25d + value * 0.5d : value <= 0.6d ? 0.30d + (value - 0.1d) * 0.6d : 0.60d + (value - 0.6d) * (0.2d / 0.3d));
    }
}
