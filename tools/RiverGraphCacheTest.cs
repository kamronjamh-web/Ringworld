using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Loading;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T2R-9 (docs/work-orders/terrain-vegetation/T2.md, S12): the river graph store and its lifecycle on strip graphs (the store's bake hook returns a 40 km strip; the file format, header checks, atomic write,
/// de-duplication and cancellation are the production ones). Checks: disk round trip; a stale format and a truncated file are rejected and rebaked; a valid file is never rebaked or rewritten; two identities never
/// share an entry or a file; a world without rivers creates no file and no wait; two concurrent callers build once; a caller that leaves does not cancel a build another caller still waits for; cancellation
/// leaves no file and no temp; a runtime sampler of a river world refuses to be built without a graph and, after the loading bridge baked one, binds it. Negative controls: a corrupted identity and a corrupted content hash are
/// detected (the store bakes instead of loading). Prints RIVER_GRAPH_CACHE=PASS|FAIL. Headless: tools/run_tests.ps1 -Tests river_graph_cache_test.
/// </summary>
public partial class RiverGraphCacheTest : RefCounted
{
    private const int Seed = 1337;
    private readonly List<string> _failures = new();
    private int _overrideCalls;
    private TerrainGenerationParameters _a = null!, _b = null!;
    private VoxelTerrainSettings _settingsA = null!, _settingsB = null!;

    private void Fail(string message) { if (_failures.Count < 30) _failures.Add(message); }
    private void Check(bool ok, string message) { if (!ok) Fail(message); }

    public int Run()
    {
        string root = ProjectSettings.GlobalizePath("user://t2r9_graph_cache_test");
        string previousWorld = ProjectSettings.GetSetting("ringworld/runtime/active_world_directory", "").AsString();
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            _a = EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1, new[] { "water.rivers" });
            _b = _a.WithFields(new Dictionary<string, double> { ["river_density"] = 1.5 });
            _settingsA = EmitterSiteTestWorlds.CreateProductionSettings(Seed, _a);
            _settingsB = EmitterSiteTestWorlds.CreateProductionSettings(Seed, _b);
            RiverGraphStore.BakeOverride = StripBake;
            RoundTripAndRejections(Path.Combine(root, "world"));
            IdentitiesAndGenerationOne(Path.Combine(root, "cache"));
            Concurrency(Path.Combine(root, "concurrent"));
            Cancellation(Path.Combine(root, "cancel"));
            RuntimeBinding(Path.Combine(root, "runtime"));
        }
        catch (Exception e) { Console.Error.WriteLine("RIVER_GRAPH_CACHE=FAIL exception " + e); return 1; }
        finally
        {
            RiverGraphStore.BakeOverride = null;
            RiverGraphStore.Clear();
            ProjectSettings.SetSetting("ringworld/runtime/active_world_directory", previousWorld);
            _settingsA?.Dispose();
            _settingsB?.Dispose();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
        if (_failures.Count != 0) { Console.Error.WriteLine("RIVER_GRAPH_CACHE=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("RIVER_GRAPH_CACHE=PASS");
        return 0;
    }

    /// <summary>Strip bake standing in for the whole-ring bake; counts its calls.</summary>
    private (RiverGraph Graph, byte Source) StripBake(HydrologyBakeInputs inputs, ulong identity, CancellationToken ct, IProgress<double>? progress)
    {
        Interlocked.Increment(ref _overrideCalls);
        progress?.Report(0.5);
        RiverGraph graph = RiverTestGraphs.Strip(inputs.Parameters, inputs.Parameters == _b ? _settingsB : _settingsA, 10_134_000d, 40, 2, identity);
        ct.ThrowIfCancellationRequested();
        return (graph, 0);
    }

    private HydrologyBakeInputs Inputs(TerrainGenerationParameters recipe, string directory, bool worldFolder)
    {
        HydrologyBakeInputs captured = HydrologyBakeInputs.Capture(recipe == _b ? _settingsB : _settingsA, recipe, 2, HydrologySourcePreference.Cpu, directory);
        return new HydrologyBakeInputs(recipe, captured.Climate, directory, worldFolder, 2, false, HydrologySourcePreference.Cpu);
    }

    private static string[] Files(string directory) => Directory.Exists(directory) ? Directory.GetFiles(directory) : Array.Empty<string>();

    // ------------------------------------------------------------------ file

    private void RoundTripAndRejections(string directory)
    {
        HydrologyBakeInputs inputs = Inputs(_a, directory, true);
        ulong identity = inputs.Identity;
        string path = inputs.FilePath(identity);
        Check(path == Path.Combine(directory, $"river_graph_v{HydrologyVersions.GraphFormat}.bin"), "world file name differs from river_graph_v<format>.bin: " + path);

        RiverGraphStore.Clear(); _overrideCalls = 0;
        RiverGraph first = RiverGraphStore.GetOrBuild(inputs);
        Check(_overrideCalls == 1 && RiverGraphStore.BakeCount == 1, $"first get did not bake once (calls {_overrideCalls})");
        Check(File.Exists(path) && Files(directory).Length == 1, "the bake did not leave exactly the graph file: " + string.Join(", ", Files(directory)));
        byte[] bytes = File.ReadAllBytes(path);
        Check(RiverGraphFormat.TryRead(bytes, identity, out RiverGraph? read, out string error) && read != null && read.IdentityHash == first.IdentityHash, "the written file does not read back as the baked graph: " + error);
        Check(first.InputIdentity == identity, "the graph does not carry the pre-graph identity");
        Check(RiverGraphStore.TryGet(identity, out RiverGraph? kept) && ReferenceEquals(kept, first), "the baked graph is not resident under its identity");
        Check(ReferenceEquals(RiverGraphStore.GetOrBuild(inputs), first) && _overrideCalls == 1, "a resident graph was baked again");

        // A valid file is read, never rebaked and never rewritten.
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        Thread.Sleep(30);
        RiverGraphStore.Clear(); _overrideCalls = 0;
        RiverGraph loaded = RiverGraphStore.GetOrBuild(inputs);
        Check(_overrideCalls == 0 && RiverGraphStore.LoadCount == 1 && RiverGraphStore.BakeCount == 0, $"a valid file was not simply loaded (bake calls {_overrideCalls}, loads {RiverGraphStore.LoadCount})");
        Check(loaded.IdentityHash == first.IdentityHash && RiverGraphFormat.Write(loaded).AsSpan().SequenceEqual(bytes), "the loaded graph is not byte-equal to the baked one");
        Check(File.GetLastWriteTimeUtc(path) == stamp && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes), "a valid file was rewritten");
        Check(RiverGraphStore.LastStats is { Loaded: true }, "the load left no stats");

        // Rejections: each corrupt file is replaced by a fresh bake.
        void Rebakes(string label, byte[] corrupt, bool mustBeInvalid = true)
        {
            if (mustBeInvalid) Check(!RiverGraphFormat.TryRead(corrupt, identity, out _, out _), $"{label}: TryRead accepted the corrupt file");
            File.WriteAllBytes(path, corrupt);
            RiverGraphStore.Clear(); _overrideCalls = 0;
            RiverGraph again = RiverGraphStore.GetOrBuild(inputs);
            Check(_overrideCalls == 1 && RiverGraphStore.LoadCount == 0, $"{label}: the store loaded a corrupt file instead of baking (bake calls {_overrideCalls})");
            Check(again.IdentityHash == first.IdentityHash && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes), $"{label}: the corrupt file was not replaced by the valid one");
            Check(Files(directory).Length == 1, $"{label}: a temp file is left: " + string.Join(", ", Files(directory)));
        }
        byte[] stale = (byte[])bytes.Clone();
        BitConverter.GetBytes(HydrologyVersions.GraphFormat - 1).CopyTo(stale, 4);
        Rebakes("stale graph format", stale);
        byte[] staleAlgorithm = (byte[])bytes.Clone();
        BitConverter.GetBytes(HydrologyVersions.DrainageAlgorithm - 1).CopyTo(staleAlgorithm, 8);
        Rebakes("stale drainage algorithm", staleAlgorithm);
        Rebakes("truncated file", bytes.AsSpan(0, bytes.Length / 2).ToArray());
        byte[] wrongIdentity = (byte[])bytes.Clone();
        wrongIdentity[12] ^= 0x01; // negative control: a corrupted identity hash in the header is detected
        Rebakes("corrupted identity hash", wrongIdentity);
        byte[] wrongContent = (byte[])bytes.Clone();
        wrongContent[RiverGraphFormat.HeaderBytes + (bytes.Length - RiverGraphFormat.HeaderBytes) / 2] ^= 0x10;
        Rebakes("corrupted payload", wrongContent);
        byte[] wrongHash = (byte[])bytes.Clone();
        wrongHash[29] ^= 0x01;
        Rebakes("corrupted content hash", wrongHash);
        Console.WriteLine($"RIVER_GRAPH_CACHE_DETAIL round trip: file {bytes.Length} bytes, identity {identity:X16}, graph hash {first.IdentityHash:X16}");
    }

    // ------------------------------------------------------------------ identities, generation 1

    private void IdentitiesAndGenerationOne(string directory)
    {
        HydrologyBakeInputs a = Inputs(_a, directory, false), b = Inputs(_b, directory, false);
        Check(a.Identity != b.Identity, "two recipes share one identity");
        RiverGraphStore.Clear(); _overrideCalls = 0;
        RiverGraph ga = RiverGraphStore.GetOrBuild(a), gb = RiverGraphStore.GetOrBuild(b);
        Check(_overrideCalls == 2, $"two identities did not bake twice (calls {_overrideCalls})");
        Check(!ReferenceEquals(ga, gb) && ga.InputIdentity == a.Identity && gb.InputIdentity == b.Identity, "two identities share one graph entry");
        Check(RiverGraphStore.TryGet(a.Identity, out RiverGraph? ra) && ReferenceEquals(ra, ga) && RiverGraphStore.TryGet(b.Identity, out RiverGraph? rb) && ReferenceEquals(rb, gb), "the store does not key by identity");
        Check(a.FilePath(a.Identity) != b.FilePath(b.Identity) && File.Exists(a.FilePath(a.Identity)) && File.Exists(b.FilePath(b.Identity)) && Files(directory).Length == 2, "two identities do not have their own file in the cache folder: " + string.Join(", ", Files(directory)));
        Check(!RiverGraphStore.TryGet(a.Identity ^ 1UL, out _), "an unknown identity was found");

        // A generation-1 world (no water.rivers) bakes nothing, writes nothing and has nothing to wait for.
        string gen1Directory = Path.Combine(directory, "gen1");
        using VoxelTerrainSettings gen1 = EmitterSiteTestWorlds.CreateProductionSettings(Seed, EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1));
        RiverGraphStore.Clear(); _overrideCalls = 0;
        Check(HydrologyBakeInputs.CaptureForWorld(gen1) == null, "a generation-1 world captured river inputs");
        Check(HydrologyBakeInputs.CaptureForWorld(null) == null && HydrologyBakeInputs.CaptureForWorld(new VoxelTerrainSettings()) == null, "a world without a recipe captured river inputs");
        var bridge = new RingWorldLoadingBridge();
        var world = new Node();
        var grid = new VoxelChunkGridManager { Name = "ChunkGridManager", TerrainSettings = gen1 };
        world.AddChild(grid);
        bridge.PrepareWorld(world);
        Godot.Collections.Dictionary begin = bridge.BeginHydrology();
        Godot.Collections.Dictionary status = bridge.CaptureHydrologyStatus();
        Check(!begin["required"].AsBool() && !status["required"].AsBool() && status["completed"].AsBool(), "a generation-1 world has a river stage");
        Check(_overrideCalls == 0 && !Directory.Exists(gen1Directory) && RiverGraphStore.BakeCount == 0, "a generation-1 world baked or created a file");
        world.RemoveChild(grid);
        grid.TerrainSettings = null;
        grid.Free(); world.Free(); bridge.Free();
    }

    // ------------------------------------------------------------------ concurrency

    private void Concurrency(string directory)
    {
        HydrologyBakeInputs inputs = Inputs(_a, directory, true);
        RiverGraphStore.Clear(); _overrideCalls = 0;
        var gate = new ManualResetEventSlim(false);
        Func<HydrologyBakeInputs, ulong, CancellationToken, IProgress<double>?, (RiverGraph, byte)> slow = (i, id, ct, p) => { gate.Wait(TimeSpan.FromSeconds(30)); return StripBake(i, id, ct, p); };
        RiverGraphStore.BakeOverride = slow;
        try
        {
            Task<RiverGraph> t1 = RiverGraphStore.GetOrBuildAsync(inputs), t2 = RiverGraphStore.GetOrBuildAsync(inputs);
            Thread.Sleep(100);
            gate.Set();
            RiverGraph g1 = t1.GetAwaiter().GetResult(), g2 = t2.GetAwaiter().GetResult();
            Check(_overrideCalls == 1 && RiverGraphStore.BakeCount == 1, $"two concurrent callers built {_overrideCalls} times");
            Check(ReferenceEquals(g1, g2), "two concurrent callers got different graphs");

            // One waiter leaves while another stays: the build goes on.
            RiverGraphStore.Clear(); _overrideCalls = 0; gate.Reset();
            File.Delete(inputs.FilePath(inputs.Identity));
            using var leaver = new CancellationTokenSource();
            Task<RiverGraph> stay = RiverGraphStore.GetOrBuildAsync(inputs), leave = RiverGraphStore.GetOrBuildAsync(inputs, leaver.Token);
            Thread.Sleep(100);
            leaver.Cancel();
            Check(Await(leave) is OperationCanceledException, "the cancelled waiter did not see a cancellation");
            gate.Set();
            RiverGraph survived = stay.GetAwaiter().GetResult();
            Check(_overrideCalls == 1 && survived.InputIdentity == inputs.Identity && File.Exists(inputs.FilePath(inputs.Identity)), "the build was cancelled although a waiter stayed");
        }
        finally { RiverGraphStore.BakeOverride = StripBake; gate.Set(); }
    }

    private static Exception? Await(Task task)
    {
        try { task.GetAwaiter().GetResult(); return null; }
        catch (Exception e) { return e; }
    }

    // ------------------------------------------------------------------ cancellation

    private void Cancellation(string directory)
    {
        HydrologyBakeInputs inputs = Inputs(_a, directory, true);
        RiverGraphStore.Clear(); _overrideCalls = 0;
        bool sawToken = false;
        RiverGraphStore.BakeOverride = (i, id, ct, p) =>
        {
            Interlocked.Increment(ref _overrideCalls);
            sawToken = ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("the bake was not cancelled");
        };
        try
        {
            using var cancel = new CancellationTokenSource();
            Task<RiverGraph> task = RiverGraphStore.GetOrBuildAsync(inputs, cancel.Token);
            Thread.Sleep(100);
            cancel.Cancel();
            Exception? error = Await(task);
            Thread.Sleep(200);
            Check(error is OperationCanceledException && sawToken, "the last waiter leaving did not cancel the build: " + error?.GetType().Name);
            Check(Files(directory).Length == 0, "a cancelled bake left files: " + string.Join(", ", Files(directory)));
            Check(!RiverGraphStore.TryGet(inputs.Identity, out _), "a cancelled bake became resident");
            // The next caller builds afresh.
            RiverGraphStore.BakeOverride = StripBake;
            RiverGraph again = RiverGraphStore.GetOrBuild(inputs);
            Check(again.InputIdentity == inputs.Identity && File.Exists(inputs.FilePath(inputs.Identity)) && Files(directory).Length == 1, "the build after a cancelled one did not complete cleanly");
        }
        finally { RiverGraphStore.BakeOverride = StripBake; }
    }

    // ------------------------------------------------------------------ runtime

    /// <summary>A runtime sampler of a river world needs its graph. The loading bridge captures the inputs on the main thread, bakes on a worker, writes the world's file, and the sampler then binds that graph.</summary>
    private void RuntimeBinding(string worldDirectory)
    {
        RiverGraphStore.Clear(); _overrideCalls = 0;
        ProjectSettings.SetSetting("ringworld/runtime/active_world_directory", worldDirectory);
        RingTerrainSampler Sampler() => new(_settingsA, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
        bool threw = false;
        try { using RingTerrainSampler none = Sampler(); }
        catch (InvalidOperationException e) { threw = e.Message.Contains("river graph not baked", StringComparison.Ordinal); }
        Check(threw, "a river world's sampler was built without a baked graph");

        var bridge = new RingWorldLoadingBridge();
        var world = new Node();
        var grid = new VoxelChunkGridManager { Name = "ChunkGridManager", TerrainSettings = _settingsA };
        world.AddChild(grid);
        bridge.PrepareWorld(world);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Godot.Collections.Dictionary begin = bridge.BeginHydrology();
        double beginMs = clock.Elapsed.TotalMilliseconds;
        Check(begin["required"].AsBool(), "a river world has no river stage");
        Check(beginMs < 100d, $"BeginHydrology blocked the main thread for {beginMs:F0} ms (limit 100)");
        Godot.Collections.Dictionary status = bridge.CaptureHydrologyStatus();
        double progress = 0;
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!status["completed"].AsBool() && deadline.Elapsed.TotalSeconds < 120)
        {
            Thread.Sleep(20);
            status = bridge.CaptureHydrologyStatus();
            progress = Math.Max(progress, status["progress"].AsDouble());
        }
        Check(status["completed"].AsBool() && status["error"].AsString().Length == 0, "the river stage did not complete: " + status["error"].AsString());
        Check(progress > 0d, "the river stage reported no progress");
        string file = Path.Combine(worldDirectory, "hydrology", $"river_graph_v{HydrologyVersions.GraphFormat}.bin");
        Check(File.Exists(file), "the bake did not write <world>/hydrology/river_graph_v<format>.bin");
        ulong identity = HydrologyBakeIdentity.Compute(_a);
        Check(RiverGraphStore.TryGet(identity, out RiverGraph? graph) && graph != null && _overrideCalls == 1, $"the store holds no graph for the world (bake calls {_overrideCalls})");
        using RingTerrainSampler runtime = Sampler();
        RiverGraph? bound = runtime.RuntimeSurfaceRecipe == null ? null : RuntimeGraph(runtime);
        Check(runtime.UsesRuntimeSurfaceGeneration && bound != null && ReferenceEquals(bound, graph), "the runtime sampler did not bind the baked graph");
        using RingTerrainSampler copy = runtime.CreateIndependentCopy();
        Check(ReferenceEquals(RuntimeGraph(copy), graph), "an independent copy did not share its root's graph");
        // A failed bake reports an error and leaves no sampler-blocking state.
        RiverGraphStore.Clear();
        File.Delete(file);
        RiverGraphStore.BakeOverride = (i, id, ct, p) => throw new InvalidOperationException("bake failed on purpose");
        Godot.Collections.Dictionary second = bridge.BeginHydrology();
        Godot.Collections.Dictionary failed = bridge.CaptureHydrologyStatus();
        deadline.Restart();
        while (!failed["completed"].AsBool() && deadline.Elapsed.TotalSeconds < 30) { Thread.Sleep(20); failed = bridge.CaptureHydrologyStatus(); }
        Check(second["required"].AsBool() && failed["error"].AsString().Contains("bake failed on purpose", StringComparison.Ordinal), "a failed bake did not report an error key: " + failed["error"].AsString());
        RiverGraphStore.BakeOverride = StripBake;
        // Cancelling a stage in flight leaves the worker.
        RiverGraphStore.Clear();
        bridge.BeginHydrology();
        bridge.CancelHydrology();
        Thread.Sleep(50);
        world.RemoveChild(grid);
        grid.TerrainSettings = null;
        grid.Free(); world.Free(); bridge.Free();
        Console.WriteLine($"RIVER_GRAPH_CACHE_DETAIL runtime: BeginHydrology {beginMs:F1} ms on the main thread, graph hash {graph?.IdentityHash:X16}, sampler hash {runtime.TerrainDefinitionHash:X16}");
    }

    private static RiverGraph? RuntimeGraph(RingTerrainSampler sampler) => sampler.RuntimeRiverGraph;
}
