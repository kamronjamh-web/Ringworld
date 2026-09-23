using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>Small real-controller fixture. It supplies uploaded-proxy metadata only; it never starts terrain streaming.</summary>
public partial class WaterFftDemandIntegrationSmoke : Node
{
    private VoxelChunkGridManager? _grid;
    private RingVoxelExplorerController? _explorer;
    private RingTerrainProxySector? _proxy;
    private RingTerrainProxySector? _duplicateProxy;
    private RingWaterFftController? _controller;
    private readonly RingTerrainSectorCoordinate _near = new(0, 0), _far = new(3, 0), _farMore = new(4, 0), _lake = new(1, 0);
    private int _frame, _stageFrames;
    private bool _stageEntered = true;
    private double _stageSeconds;
    private long _idleSettledUpdates;
    private float _farTarget;
    private long _stageStartUpdates;
    private readonly Godot.Collections.Array<Godot.Collections.Dictionary> _stageReports = new();
    private string _stage = "setup";
    private readonly List<double> _demandMs = new();
    public bool Completed { get; private set; }
    public bool Passed { get; private set; }
    public string LastFailure { get; private set; } = string.Empty;

    public override void _Ready()
    {
        if (RenderingServer.GetRenderingDevice() is null) { Fail("unsupported GPU renderer"); return; }
        _grid = new VoxelChunkGridManager { Name = "ChunkGridManager", DormantEditableBackend = true, BuildOnReady = false, EnableFloatingOriginBridge = false, RingAcrossWidthMeters = 4096, UseDeterministicTerrainGeneration = true, TerrainSettings = new VoxelTerrainSettings() };
        AddChild(_grid);
        _grid.ConfigureRuntimeRingTopology(2048, 65536.0 / Math.Tau);
        using (RingTerrainSampler? copy = _grid.CreateIndependentTerrainSampler()) { if (copy is null) { Fail("grid sampler did not initialize"); return; } }
        _explorer = new RingVoxelExplorerController { Name = "Explorer", GridManagerPath = "../ChunkGridManager" };
        AddChild(_explorer); _explorer.SetPhysicsProcess(false); _explorer.SetProcessInput(false);
        float sea = _grid.TerrainSampler!.SeaLevelY + 6f;
        _explorer.SetBenchmarkCanonicalPosition(new RingCanonicalPosition(0, sea, 128), 0, true);
        _proxy = new RingTerrainProxySector { Name = "FixtureProxy", CoordinatorManaged = true, GridManagerPath = "__fixture_no_grid__", FocusPath = "__fixture_no_focus__", SectorSizeMeters = 256, FadeOutEndMeters = 2000, EnableFarFade = false };
        AddChild(_proxy); _proxy.ConfigureWaterDemandFixture(_grid);
        _controller = new RingWaterFftController { Name = "WaterFft", GridManagerPath = "../ChunkGridManager", ExplorerPath = "../Explorer", Resolution = ResolutionFromArgs(), CascadeCount = CascadesFromArgs(), AdaptiveUpdatesEnabled = true, MinimumUpdatesPerSecond = 3, FullRateDistanceMeters = 160, FullRateAreaSquareKilometers = .25f, IdleGraceSeconds = .15f, GeometryUpdatesPerSecond = 30, UpdatesPerSecond = 9, DetailDistanceMeters = 1800, GeometryDistanceMeters = 400, FollowWeatherWind = false, RequireShorelineField = false };
        AddChild(_controller);
    }

    public override void _Process(double delta)
    {
        if (Completed || _controller is null || _proxy is null) return;
        if (++_frame > 1800) { Fail("timeout at " + _stage); return; }
        _demandMs.Add(_controller.WaterDemandCheckMilliseconds);
        _stageSeconds += Math.Max(0, delta);
        string processedStage = _stage;
        switch (_stage)
        {
            case "setup":
                if (_stageEntered) _proxy.SetWaterDemandFixtureSector(_lake, new Rect2(0, 0, 1, 1), SurfaceWaterKind.Lake);
                if (_stageSeconds > .5) { Check(_controller.RelevantWaterCellCount == 0, "lake metadata was treated as FFT ocean demand"); _proxy.RemoveWaterDemandFixtureSector(_lake); VerifyCollectorCounterpoints(); Next("dry"); }
                break;
            case "dry":
                if (_stageSeconds > 1) { Check(_controller.RelevantWaterCellCount == 0 && _controller.CompletedFftUpdates == 0, "dry startup dispatched FFT work"); Next("far"); }
                break;
            case "far":
                if (_stageEntered) _proxy.SetWaterDemandFixtureSector(_far, new Rect2(0, 0, .25f, .25f), SurfaceWaterKind.Ocean);
                if (_stageSeconds > 2.0) { Check(_controller.RelevantWaterCellCount > 0, "far submitted ocean was not collected"); _farTarget = _controller.TargetDetailUpdatesPerSecond; Next("more"); }
                break;
            case "more":
                if (_stageEntered) _proxy.SetWaterDemandFixtureSector(_farMore, new Rect2(0, 0, 1, 1), SurfaceWaterKind.Ocean);
                if (_stageSeconds > 2.0) { Check(_controller.RelevantWaterSectorCount >= 2 && _controller.TargetDetailUpdatesPerSecond > _farTarget, "more far water did not increase adaptive detail rate"); Next("near"); }
                break;
            case "near":
                if (_stageEntered) _proxy.SetWaterDemandFixtureSector(_near, new Rect2(0, 0, 1, 1), SurfaceWaterKind.Ocean);
                if (_stageSeconds > 2.0) { Check(_controller.WaterDemandStatus == "full" && _controller.CompletedFftUpdates > 0, "near water did not reach full real FFT scheduling"); Next("idle"); }
                break;
            case "idle":
                if (_stageEntered) { _proxy.SetWaterDemandFixtureVisible(_near, false); _proxy.SetWaterDemandFixtureVisible(_far, false); _proxy.SetWaterDemandFixtureVisible(_farMore, false); }
                if (_stageSeconds > .5 && _idleSettledUpdates == 0) _idleSettledUpdates = _controller.CompletedFftUpdates;
                if (_stageSeconds > 2.0) { Check(_controller.WaterDemandStatus == "dormant" && _controller.RelevantWaterCellCount == 0 && _controller.CompletedFftUpdates == _idleSettledUpdates, "hidden water did not reach stable dormant FFT state"); Next("wake"); }
                break;
            case "wake":
                if (_stageEntered) _proxy.SetWaterDemandFixtureVisible(_near, true);
                if (_stageSeconds > 2.0) { Check(_controller.CompletedFftUpdates > _idleSettledUpdates && _controller.WaterDemandStatus == "full", "visible water did not wake controller"); Next("complete"); Pass(); }
                break;
        }
        if (_stage == processedStage) _stageEntered = false;
    }
    private void Next(string stage)
    {
        if (_controller is not null && _stage != "setup") _stageReports.Add(new Godot.Collections.Dictionary {
            ["stage"] = _stage, ["seconds"] = _stageSeconds, ["completed_updates"] = _controller.CompletedFftUpdates - _stageStartUpdates,
            ["completed_hz"] = (_controller.CompletedFftUpdates - _stageStartUpdates) / Math.Max(.001, _stageSeconds),
            ["geometry_hz"] = _controller.TargetGeometryUpdatesPerSecond, ["detail_hz"] = _controller.TargetDetailUpdatesPerSecond,
            ["sectors"] = _controller.RelevantWaterSectorCount, ["area_m2"] = _controller.RelevantWaterAreaSquareKilometers * 1_000_000d });
        _stage = stage; _stageFrames = 0; _stageEntered = true; _stageSeconds = 0; _stageStartUpdates = _controller?.CompletedFftUpdates ?? 0;
    }
    private void VerifyCollectorCounterpoints()
    {
        if (_grid is null || _proxy is null) { Fail("fixture grid unavailable"); return; }
        var position = new RingCanonicalPosition(8, _grid.TerrainSampler!.SeaLevelY + 6, 128);
        _proxy.SetWaterDemandFixtureSector(_near, new Rect2(0, 0, 1, 1), SurfaceWaterKind.Ocean);
        RingWaterFftDemandSnapshot baseDemand = RingTerrainProxySector.CaptureFftWaterDemand(position, 1000);
        Check(baseDemand.HasDemand, "submitted ocean was not collected");
        RingWaterFftDemandSnapshot high = RingTerrainProxySector.CaptureFftWaterDemand(position with { VerticalMeters = position.VerticalMeters + 5000 }, 1000);
        Check(!high.HasDemand, "high altitude retained water demand");
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        RingWaterFftDemandSnapshot seam = RingTerrainProxySector.CaptureFftWaterDemand(position with { AroundMeters = circumference - 10 }, 1000);
        Check(seam.HasDemand && seam.NearestDistanceMeters < 30, "wrapped seam demand was not nearby");
        _proxy.SetWaterDemandFixtureWarm(_near, true);
        Check(!RingTerrainProxySector.CaptureFftWaterDemand(position, 1000).HasDemand, "warm sector retained demand");
        _proxy.SetWaterDemandFixtureWarm(_near, false);
        _duplicateProxy = new RingTerrainProxySector { Name = "DuplicateFixtureProxy", CoordinatorManaged = true, GridManagerPath = "__fixture_no_grid__", FocusPath = "__fixture_no_focus__", SectorSizeMeters = 256, SampleSpacingMeters = 32, FadeOutEndMeters = 2000, EnableFarFade = false };
        AddChild(_duplicateProxy); _duplicateProxy.ConfigureWaterDemandFixture(_grid);
        _duplicateProxy.SetWaterDemandFixtureSector(_near, new Rect2(0, 0, 1, 1), SurfaceWaterKind.Ocean);
        RingWaterFftDemandSnapshot duplicate = RingTerrainProxySector.CaptureFftWaterDemand(position, 1000);
        Check(Math.Abs(duplicate.WeightedAreaSquareMeters - baseDemand.WeightedAreaSquareMeters) < 1, "duplicate LOD footprint increased demand area");
        _duplicateProxy.RemoveWaterDemandFixtureSector(_near); _duplicateProxy.QueueFree(); _duplicateProxy = null;
        _proxy.RemoveWaterDemandFixtureSector(_near);
        _proxy.SetWaterDemandFixtureSector(_near, new Rect2(.5f, .5f, .05f, .05f), SurfaceWaterKind.Ocean);
        Check(RingTerrainProxySector.CaptureFftWaterDemand(position, 1000).HasDemand, "partial ocean footprint was dropped");
        _proxy.RemoveWaterDemandFixtureSector(_near);
    }
    private void Check(bool condition, string message) { if (!condition) Fail(message); }
    private void Pass()
    {
        if (Completed) return;
        _demandMs.Sort(); double mean = 0; foreach (double value in _demandMs) mean += value; mean /= Math.Max(1, _demandMs.Count);
        double p95 = _demandMs[Math.Min(_demandMs.Count - 1, (int)(_demandMs.Count * .95))];
        var report = new Godot.Collections.Dictionary { ["resolution"] = ResolutionFromArgs(), ["cascades"] = CascadesFromArgs(), ["completed_updates"] = _controller?.CompletedFftUpdates ?? 0, ["demand_check_ms_mean"] = mean, ["demand_check_ms_p95"] = p95, ["far_detail_hz"] = _farTarget, ["near_detail_hz"] = _controller?.TargetDetailUpdatesPerSecond ?? 0f, ["stages"] = _stageReports };
        DirAccess.MakeDirAbsolute("res://artifacts");
        using FileAccess output = FileAccess.Open($"res://artifacts/water-adaptive-demand-integration-res{ResolutionFromArgs()}.json", FileAccess.ModeFlags.Write);
        output.StoreString(Json.Stringify(report));
        GD.Print("WATER_FFT_DEMAND_INTEGRATION=PASS " + Json.Stringify(report)); Passed = true; Completed = true;
    }
    private void Fail(string reason) { if (Completed) return; LastFailure = reason; Completed = true; GD.PushError("WATER_FFT_DEMAND_INTEGRATION=FAIL " + reason); }
    private static bool Is1024() => Array.IndexOf(OS.GetCmdlineArgs(), "--adaptive-fft-resolution=1024") >= 0 || Array.IndexOf(OS.GetCmdlineUserArgs(), "--adaptive-fft-resolution=1024") >= 0;
    private static int ResolutionFromArgs() => Is1024() ? 1024 : 128;
    private static int CascadesFromArgs() => Is1024() ? 4 : 2;
}
