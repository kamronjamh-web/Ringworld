using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tests;

/// <summary>Rendered, fixed-fixture diagnostic for production imported-tree GPU compaction and indirect draws.</summary>
public partial class TreeGpuBenchmark : Node3D
{
    public string Result { get; private set; } = "RUNNING";
    private const int Variants = 30, SpatialGroups = 12, RootsPerGroup = 182, ExtraRoots = 16, WarmupFrames = 60, TimedFrames = 180, Repetitions = 3;
    private readonly RingPerformanceTelemetry.Completed[] _drain = new RingPerformanceTelemetry.Completed[512];
    private readonly List<object> _treeSamples = new();
    private readonly HashSet<long> _treeTimestampFrames = new();
    private RingBiomeDecorationLayer? _layer;
    private Camera3D? _camera;
    private DirectionalLight3D? _sun;
    private readonly List<uint[]> _frozenPopulation = new();
    private int _protectedRootCount;
    private int _timestampDrops;
    private readonly TreeRenderMode _renderMode = ResolveRenderMode();

    private enum TreeRenderMode { DefaultLegacy, Before, After }
    private static TreeRenderMode ResolveRenderMode()
    {
        string[] args = OS.GetCmdlineUserArgs();
        if (args.Contains("--tree-render-before")) return TreeRenderMode.Before;
        if (args.Contains("--tree-render-after")) return TreeRenderMode.After;
        return TreeRenderMode.DefaultLegacy;
    }
    private bool IsAfterRenderMode => _renderMode == TreeRenderMode.After;
    private string ArtifactPath => _renderMode switch
    {
        TreeRenderMode.Before => "res://artifacts/tree-render-optimization/gpu-before",
        TreeRenderMode.After => "res://artifacts/tree-render-optimization/gpu-after",
        _ => "res://artifacts/tree-gpu-benchmark"
    };

    public async void Start()
    {
        try
        {
            if (RenderingServer.GetRenderingDevice() == null) throw new InvalidOperationException("Forward+ RenderingDevice required");
            Engine.MaxFps = 0; DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            MakeWorld(); BuildFixture();
            RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
            await Settle(240);
            await VerifyPopulation();
            await Measure();
            WriteArtifacts(); await Cleanup(); Result = "PASS wrote " + ArtifactPath + "/report.json";
        }
        catch (Exception error) { Result = "FAIL " + error.GetType().Name + ": " + error.Message; }
        finally { RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), false); RingPerformanceGpu.Configure(false); RingPerformanceTelemetry.Configure(false); _layer?.QueueFree(); }
    }

    private void MakeWorld()
    {
        ProjectSettings.SetSetting("rendering/lights_and_shadows/directional_shadow/size", 8192);
        _camera = new Camera3D { Position = new Vector3(0, 110, 360), Current = true, Fov = 62, Far = 8_000 }; AddChild(_camera); _camera.LookAtFromPosition(_camera.Position, Vector3.Zero);
        var environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(.11f, .16f, .22f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(.62f, .70f, .82f), AmbientLightEnergy = .65f };
        AddChild(new WorldEnvironment { Environment = environment });
        _sun = new DirectionalLight3D { RotationDegrees = new Vector3(-48, -28, 0), LightEnergy = 1.25f, ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits, DirectionalShadowMaxDistance = 4000 }; AddChild(_sun);
        var ground = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(1_600, 1_600) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(.17f, .23f, .15f), Roughness = 1.0f } };
        AddChild(ground);
    }

    private void BuildFixture()
    {
        IReadOnlyList<ImportedTreeVariant> catalog = ImportedTreeCatalog.Load();
        if (catalog.Count != Variants) throw new InvalidOperationException($"catalog variants={catalog.Count}, expected {Variants}");
        foreach (ImportedTreeVariant variant in catalog) foreach (ShaderMaterial material in variant.Materials)
        {
            material.SetShaderParameter("ring_center", Vector3.Zero); material.SetShaderParameter("sun_direction", _sun!.GlobalBasis.Z);
            material.SetShaderParameter("day_start_dot", -1.0f); material.SetShaderParameter("day_full_dot", -.99f);
            material.SetShaderParameter("vegetation_wind_enabled", false);
        }
        RenderingServer.GlobalShaderParameterSet("ring_local_wind_enabled", 0.0f);
        RenderingServer.GlobalShaderParameterSet("ringworld_solar_visibility", 1.0f);
        int groups = Variants * SpatialGroups;
        var transforms = new Transform3D[groups][]; var ranks = new float[groups][]; var protectedRoots = new bool[groups][];
        var around = new long[groups]; var across = new int[groups]; var groupVariants = new int[groups];
        for (int group = 0; group < groups; group++)
        {
            int variant = group % Variants, tile = group / Variants, count = RootsPerGroup + (group < ExtraRoots ? 1 : 0);
            transforms[group] = new Transform3D[count]; ranks[group] = new float[count]; protectedRoots[group] = new bool[count];
            int band = tile % 5; float radius = new[] { 160f, 350f, 730f, 1_350f, 2_500f }[band];
            float angle = Mathf.Tau * tile / SpatialGroups; Vector2 rawCenter = tile == 0 ? new Vector2(-120, 360) : new(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
            int tileX = Mathf.FloorToInt(rawCenter.X / 240), tileZ = Mathf.FloorToInt(rawCenter.Y / 240);
            Vector2 center = new((tileX + .5f) * 240, (tileZ + .5f) * 240);
            around[group] = tileX; across[group] = tileZ; groupVariants[group] = variant;
            for (int root = 0; root < count; root++)
            {
                int column = root % 14, row = root / 14;
                float jitterX = ((variant * 29 + root * 11) % 9 - 4) * .7f, jitterZ = ((variant * 17 + root * 7) % 9 - 4) * .7f;
                float x = center.X + (column - 6.5f) * 11 + jitterX, z = center.Y + (row - 6) * 11 + jitterZ;
                float scale = 7.0f + (root % 5) * .75f; float yaw = (variant * 37 + root * 17) % 360;
                transforms[group][root] = new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(yaw)).Scaled(Vector3.One * scale), new Vector3(x, 0, z));
                ranks[group][root] = root / (float)count;
                protectedRoots[group][root] = _protectedRootCount < 192 && new Vector2(x + 12, z - 360).Length() < 110;
                if (protectedRoots[group][root]) _protectedRootCount++;
            }
        }
        _layer = new RingBiomeDecorationLayer { UseGpuCulling = true, SkipUnchangedGpuCulling = true, MergeFarGpuTreeBatches = true,
            UseSharedFarTreeImpostors = true, OptimizeGpuTreeDispatches = true,
            UseCompactTreeLodProfile = IsAfterRenderMode, MergeMidGpuTreeBatches = IsAfterRenderMode,
            PruneEmptyGpuTreeBatches = IsAfterRenderMode, SimplifyDistantTreeMaterials = IsAfterRenderMode,
            UseLowDetailTreeShadows = IsAfterRenderMode,
            MaximumGpuTreeRoots = 65_536, MaximumGpuTreeBatches = 1024, MaximumLod1Trees = 256, MaximumLod2Trees = 640,
            MaximumLod3Trees = 1800, MaximumLod4Trees = 5200, MaximumTreeBillboards = 15000, MaximumVegetationUploadsPerFrame = 512,
            Lod1DistanceMeters = 240, Lod2DistanceMeters = 480, Lod3DistanceMeters = 960, MinimumLod5DistanceMeters = 1920, MaximumLod5DistanceMeters = 5500,
            TreeSunShadowDistanceMeters = 480, MaximumTreeSunShadowBatches = 128 };
        // Fixtures are deliberately guarded from the compact profile.  Opt-in
        // after-mode bypasses only that test guard while retaining the real
        // imported 30-variant catalog and every original population setting.
        _layer.ForceCompactTreeLodProfileForTests = IsAfterRenderMode;
        if (_protectedRootCount == 0) throw new InvalidOperationException("fixture has no near protected roots");
        AddChild(_layer); _layer.ConfigureGpuTreeBenchmarkFixture(catalog, transforms, ranks, protectedRoots, around, across, groupVariants); _layer.Visible = true;
        _layer.ProcessMode = ProcessModeEnum.Disabled; // The benchmark owns all fixture servicing below.
    }

    private async Task Settle(int limit)
    {
        int stable = 0;
        for (int frame = 0; frame < limit; frame++) { Service(_camera!.Position); await Frame(); stable = _layer!.GpuTreeCullingSettled && _layer.GpuTreeCompletedUpdates > 0 && _layer.GpuTreeFallbackBatchCount == 0 ? stable + 1 : 0; if (stable >= 4) return; }
        throw new TimeoutException("GPU tree fixture did not settle: " + _layer!.GpuTreeCullingStatus);
    }

    private async Task<uint[]> VerifyPopulation()
    {
        // Read the settled mixed-LOD population at the common benchmark pose.
        SetStartCamera(); await Settle(180);
        var reads = new List<(int Lod, TaskCompletionSource<uint> Completion)>();
        for (int lod = 0; lod < 5; lod++)
            foreach (int batch in _layer!.GpuTreeFixtureOutputIds(lod))
            {
                var completion = new TaskCompletionSource<uint>(); reads.Add((lod, completion));
                _layer.GpuTreeFixtureReadbackBatch(batch, (commands, _) => completion.TrySetResult(SumCommands(commands)));
            }
        await Task.WhenAll(reads.Select(read => read.Completion.Task));
        var lodCounts = new uint[5]; foreach (var read in reads) lodCounts[read.Lod] += read.Completion.Task.Result;
        uint count = lodCounts.Aggregate(0u, (total, value) => total + value);
        uint cap = IsAfterRenderMode ? 256u + 2696u + 5200u + 15000u : 256u + 640u + 1800u + 5200u + 15000u;
        if (count == 0 || count > cap) throw new InvalidOperationException($"indirect population {count}, expected 1..{cap}");
        _populationByLod = lodCounts;
        SetStartCamera(); await Settle(180); return lodCounts;
    }

    private async Task Measure()
    {
        var cases = new List<object>();
        for (int repetition = 1; repetition <= Repetitions; repetition++)
        {
            GD.Print($"TREE_GPU_BENCHMARK repetition {repetition}/{Repetitions}");
            string[] order = { "moving_full", "moving_frozen_outputs", "moving_frozen_no_shadows", "moving_frozen_hidden_trees", "moving_frozen_shadow64", "moving_frozen_shadow240m", "stationary_full" };
            if ((repetition & 1) == 0) Array.Reverse(order);
            foreach (string label in order) { bool stationary = label == "stationary_full"; cases.Add(await MeasureCase(label, repetition, moving: !stationary, service: label is "moving_full" or "stationary_full")); _summary = new { roots = 65_536, variants = Variants, spatial_groups = SpatialGroups, render_mode = _renderMode.ToString(), render_flags = RenderFlags(), cases }; WriteArtifacts(); }
        }
        _summary = new { roots = 65_536, variants = Variants, spatial_groups = SpatialGroups, render_mode = _renderMode.ToString(), render_flags = RenderFlags(), protected_roots = _protectedRootCount, batches = _layer!.GpuTreeBatchCount, resident_bytes = _layer.GpuTreeResidentBytes, readback_population_by_lod = _populationByLod, cases };
    }

    private object RenderFlags() => new
    {
        compact_tree_lod_profile = _layer!.UseCompactTreeLodProfile,
        compact_tree_lod_profile_forced_for_fixture = _layer.ForceCompactTreeLodProfileForTests,
        merge_mid_gpu_tree_batches = _layer.MergeMidGpuTreeBatches,
        prune_empty_gpu_tree_batches = _layer.PruneEmptyGpuTreeBatches,
        simplify_distant_tree_materials = _layer.SimplifyDistantTreeMaterials,
        low_detail_tree_shadows = _layer.UseLowDetailTreeShadows,
        optimized_dispatches = _layer.OptimizeGpuTreeDispatches,
        shared_far_impostors = _layer.UseSharedFarTreeImpostors
    };

    private async Task<object> MeasureCase(string label, int repetition, bool moving, bool service)
    {
        GD.Print($"TREE_GPU_BENCHMARK case={label} repetition={repetition} preparing");
        SetStartCamera(); _layer!.Visible = true;
        _layer.MaximumTreeSunShadowBatches = label == "moving_frozen_no_shadows" ? 0 : label == "moving_frozen_shadow64" ? 64 : 128;
        _layer.TreeSunShadowDistanceMeters = label == "moving_frozen_shadow240m" ? 240 : 480;
        _layer.ResetGpuTreeBenchmarkFrameCache(); await Settle(240); uint[] population = await VerifyPopulation();
        if (!service)
        {
            if (_frozenPopulation.Count > 0 && !_frozenPopulation[0].SequenceEqual(population)) throw new InvalidOperationException("frozen case readback populations differ");
            _frozenPopulation.Add((uint[])population.Clone());
        }
        int[] idsBefore = OutputIds();
        _layer.SetGpuTreeBenchmarkPresentation(label != "moving_frozen_hidden_trees", label == "moving_frozen_no_shadows" ? 0 : label == "moving_frozen_shadow64" ? 64 : 128,
            label == "moving_frozen_shadow240m" ? 240 : 480);
        for (int i = 0; i < WarmupFrames; i++) { if (moving) MoveCamera(i); if (service) Service(_camera!.Position); _layer.ServiceGpuTreeBenchmarkShadowMirrors(); await Frame(); }
        if (_layer.PendingGpuTreeShadowProxyCount != 0) throw new InvalidOperationException(label + " has pending shadow mirrors after warmup");
        if (!service && !idsBefore.SequenceEqual(OutputIds())) throw new InvalidOperationException("frozen indirect draw changed output IDs");
        _treeSamples.Clear(); _treeTimestampFrames.Clear(); _timestampDrops = 0;
        RingPerformanceGpu.Configure(false); RingPerformanceTelemetry.Configure(false); RingPerformanceTelemetry.Configure(true, 4096); RingPerformanceGpu.Configure(true);
        var wall = new List<double>(TimedFrames); var cpu = new List<double>(TimedFrames); var gpu = new List<double>(TimedFrames); var draws = new List<int>(TimedFrames); var primitives = new List<long>(TimedFrames);
        var updates = new List<long>(TimedFrames); var skipped = new List<long>(TimedFrames); var dispatches = new List<long>(TimedFrames); var serviceMs = new List<double>(TimedFrames);
        long updatesBeforeWindow = _layer.GpuTreeCompletedUpdates;
        long dispatchesBeforeWindow = _layer.GpuTreeBenchmarkDispatchCount;
        long barriersBeforeWindow = _layer.GpuTreeBenchmarkBarrierCount;
        for (int i = 0; i < TimedFrames; i++)
        {
            ulong begin = Time.GetTicksUsec(); if (moving) MoveCamera(i); var stopwatch = Stopwatch.StartNew(); if (service) Service(_camera!.Position); stopwatch.Stop(); serviceMs.Add(stopwatch.Elapsed.TotalMilliseconds); await Frame(); wall.Add((Time.GetTicksUsec() - begin) / 1000d);
            cpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(GetViewport().GetViewportRid())); gpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(GetViewport().GetViewportRid()));
            draws.Add((int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)); primitives.Add((long)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame));
            updates.Add(_layer.GpuTreeCompletedUpdates); skipped.Add(_layer.GpuTreeSkippedUnchangedUpdates); dispatches.Add(_layer.GpuTreeBenchmarkDispatchCount);
            RingPerformanceGpu.RequestReadback(); DrainTreeTiming();
        }
        for (int i = 0; i < 8; i++) { await Frame(); RingPerformanceGpu.RequestReadback(); DrainTreeTiming(); }
        if (service && !moving && _layer.GpuTreeCompletedUpdates != updatesBeforeWindow) throw new InvalidOperationException("stationary GPU tree path unexpectedly dispatched");
        if (!service && _layer.GpuTreeCompletedUpdates != updatesBeforeWindow) throw new InvalidOperationException(label + " changed compute updates while frozen");
        if (moving && service && _treeTimestampFrames.Count == 0) throw new InvalidOperationException("moving GPU case produced no tree timestamp samples");
        if (!service && _treeTimestampFrames.Count != 0) throw new InvalidOperationException(label + " produced compute timestamps while frozen");
        if (!service && !idsBefore.SequenceEqual(OutputIds())) throw new InvalidOperationException(label + " changed fixture output IDs");
        if (gpu.Any(value => !double.IsFinite(value) || value <= 0)) throw new InvalidOperationException(label + " has invalid viewport GPU measurements");
        SaveScreenshot(label, repetition);
        return new { label, repetition, render_mode = _renderMode.ToString(), merge_far = true, skip_unchanged = true, moving, frozen = !service, output_ids = idsBefore,
            optimized_dispatches = _layer.OptimizeGpuTreeDispatches, shared_far_impostors = _layer.UseSharedFarTreeImpostors,
            compact_tree_lod_profile = _layer.UseCompactTreeLodProfile, compact_tree_lod_profile_forced_for_fixture = _layer.ForceCompactTreeLodProfileForTests,
            merge_mid_gpu_tree_batches = _layer.MergeMidGpuTreeBatches, prune_empty_gpu_tree_batches = _layer.PruneEmptyGpuTreeBatches,
            simplify_distant_tree_materials = _layer.SimplifyDistantTreeMaterials, low_detail_tree_shadows = _layer.UseLowDetailTreeShadows,
            movement_threshold_m = _layer.TreeCullingMovementThresholdMeters, maximum_interval_s = _layer.TreeCullingMaximumIntervalSeconds,
            barriers_before = barriersBeforeWindow, barriers_after = _layer.GpuTreeBenchmarkBarrierCount,
            readback_population_by_lod = population, resident_roots = _layer.GpuTreeResidentRootCount, batches = _layer.GpuTreeBatchCount, updates_before = updatesBeforeWindow, updates_after = _layer.GpuTreeCompletedUpdates, dispatches_before = dispatchesBeforeWindow, dispatches_after = _layer.GpuTreeBenchmarkDispatchCount, timestamp_dropped = _timestampDrops, timestamp_source_frames = _treeTimestampFrames.Order().ToArray(), wall_ms = Stats(wall), cpu_service_ms = Stats(serviceMs), viewport_cpu_ms = Stats(cpu), viewport_gpu_ms = Stats(gpu), draw_calls = Stats(draws.Select(value => (double)value)), primitives = Stats(primitives.Select(value => (double)value)), tree_culling_timestamps = _treeSamples.ToArray(), timestamp_status = RingPerformanceGpu.Status,
            raw_frames = new { wall_ms = wall, cpu_service_ms = serviceMs, viewport_cpu_ms = cpu, viewport_gpu_ms = gpu, draw_calls = draws, primitives, completed_updates = updates, skipped_unchanged_updates = skipped, dispatches } };
    }
    private object? _summary;
    private uint[] _populationByLod = Array.Empty<uint>();
    private int[] OutputIds() => Enumerable.Range(0, 5).SelectMany(lod => _layer!.GpuTreeFixtureOutputIds(lod)).OrderBy(id => id).ToArray();
    private void MoveCamera(int frame)
    {
        float t = frame / (float)Math.Max(1, TimedFrames - 1);
        Vector3 eye = new(-12 + 24 * t, 15, 360); _camera!.LookAtFromPosition(eye, eye + new Vector3(0, -30, -600));
    }
    private void SetStartCamera() { Vector3 eye = new(-12, 15, 360); _camera!.LookAtFromPosition(eye, eye + new Vector3(0, -30, -600)); }
    private void Service(Vector3 position)
    {
        if (_renderMode == TreeRenderMode.DefaultLegacy)
            _layer!.StepGpuTreeFixture(position, Transform3D.Identity);
        else
            _layer!.StepGpuTreeFixture(position, Transform3D.Identity,
                new RingCanonicalPosition(position.X, 0, position.Z), 31_415_936d);
    }
    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    private void DrainTreeTiming()
    {
        int count = RingPerformanceTelemetry.Drain(_drain, out int dropped);
        for (int i = 0; i < count; i++) if (_drain[i].Gpu && _drain[i].Name == "GPU." + RingPerformanceGpu.TreeCulling && _treeTimestampFrames.Add(_drain[i].SourceFrameId))
            _treeSamples.Add(new { milliseconds = _drain[i].Milliseconds, source_frame = _drain[i].SourceFrameId, completed_seconds = _drain[i].CompletedSeconds });
        _timestampDrops += dropped;
    }
    private void WriteArtifacts()
    {
        string directory = ProjectSettings.GlobalizePath(ArtifactPath); Directory.CreateDirectory(directory);
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        object report = new { benchmark = "fixed rendered production imported-tree GPU/indirect-draw diagnostic; not full-game FPS", render_mode = _renderMode.ToString(), renderer = RenderingServer.GetCurrentRenderingMethod(), gpu_adapter = RenderingServer.GetVideoAdapterName(), viewport = new { width = viewport.X, height = viewport.Y }, warmup_frames = WarmupFrames, timed_frames = TimedFrames, summary = _summary };
        File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(directory, "rawdata.json"), JsonSerializer.Serialize(_summary, new JsonSerializerOptions { WriteIndented = true }));
    }
    private void SaveScreenshot(string label, int repetition)
    {
        string directory = ProjectSettings.GlobalizePath(ArtifactPath); Directory.CreateDirectory(directory);
        Image image = GetViewport().GetTexture().GetImage(); if (image.SavePng(Path.Combine(directory, $"{label}_rep{repetition}.png")) != Error.Ok) throw new IOException("screenshot write failed");
    }
    private static uint SumCommands(uint[] commands) => commands.Length >= 2 ? commands[1] : 0;
    private static object Stats(IEnumerable<double> values) { double[] sorted = values.OrderBy(value => value).ToArray(); if (sorted.Length == 0) return new { count = 0 }; double sum = sorted.Sum(); return new { count = sorted.Length, mean = sum / sorted.Length, median = sorted[sorted.Length / 2], p95 = sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * .95) - 1)], min = sorted[0], max = sorted[^1] }; }
    private async Task Cleanup()
    {
        RingPerformanceGpu.Configure(false); RingPerformanceTelemetry.Configure(false);
        if (_layer != null) { _layer.QueueFree(); _layer = null; }
        for (int i = 0; i < 12; i++) await Frame();
    }
}
