using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase5;
using RingworldTechDemo.Terrain.Editable;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Runtime high-water and rolling metrics used by certification runs.</summary>
public partial class RingCertificationMonitor : Node
{
	private readonly record struct ProxyLevelDiagnostics(
		string LodLabel,
		int ActiveSectorCount,
		int WarmSectorCount,
		int PooledSectorCount,
		int CreatedSectorNodeCount);

	private readonly record struct BenchmarkHealthSample(
		string Stage,
		double ElapsedSeconds,
		double AverageFps,
		double OnePercentLowFps,
		long WorkingSetBytes,
		long PrivateMemoryBytes,
		long ManagedHeapBytes,
		long EstimatedTerrainBytes,
		int SmoothPooledPresenterCount,
		int SmoothAuxiliaryResidentEntryCount,
		int SmoothAuxiliaryReadyResultCount,
		int SmoothTrackedTaskCount,
		int SmoothTrackedPresenterCount,
		long SmoothEstimatedResidentBytes,
		int SmoothBaseActivePresenterCount,
		int SmoothBasePooledPresenterCount,
		int SmoothNearTransitionActivePresenterCount,
		int SmoothNearTransitionPooledPresenterCount,
		int SmoothMediumActivePresenterCount,
		int SmoothMediumPooledPresenterCount,
		int SmoothBridge4ActivePresenterCount,
		int SmoothBridge4PooledPresenterCount,
		int SmoothBridge8ActivePresenterCount,
		int SmoothBridge8PooledPresenterCount,
		int SmoothBridge16ActivePresenterCount,
		int SmoothBridge16PooledPresenterCount,
		int SmoothBridge32ActivePresenterCount,
		int SmoothBridge32PooledPresenterCount,
		int SmoothCoarseActivePresenterCount,
		int SmoothCoarsePooledPresenterCount,
		bool SmoothFastTravelFocusSuspended,
		ulong SmoothFastTravelDeferredRefreshCount,
		ulong SmoothFastTravelResumeRefreshCount,
		int SmoothVisibleCount,
		int SmoothReadyCount,
		long SmoothCancellationRequests,
		double SmoothLastInstallMilliseconds,
		ulong SmoothInstallBudgetExceededFrames,
		long SmoothQueuedReadyBytes,
		long SmoothTotalEstimatedBytes,
		long SmoothPeakEstimatedBytes,
		int SmoothDeferredQueueDepth,
		int SmoothActiveWorkers,
		long TotalAllocatedBytes,
		int Gen0CollectionCount,
		int Gen1CollectionCount,
		int Gen2CollectionCount,
		double ProcessCpuMilliseconds,
		int ProcessThreadCount,
		float RenderScale,
		int SceneNodeCount,
		int ProxyActiveSectorCount,
		int ProxyWarmSectorCount,
		int ProxyPooledSectorCount,
		int ProxyCreatedSectorNodeCount,
		int ProxyRebuiltSectorMeshCount,
		int CoordinatorActiveWorkerCount,
		int CoordinatorPendingUploadCount,
		int VoxelLoadedChunkCount,
		int VoxelPooledChunkCount,
		double EngineProcessMilliseconds,
		double PhysicsProcessMilliseconds,
		double RenderSetupCpuMilliseconds,
		double ViewportRenderCpuMilliseconds,
		double ViewportRenderGpuMilliseconds,
		int RenderedObjectCount,
		long RenderedPrimitiveCount,
		int DrawCallCount,
		int ResourceCount,
		int OrphanNodeCount,
		long VideoMemoryBytes);

    [Export] public NodePath CoordinatorPath { get; set; } = new("../TerrainLodCoordinator");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath GlobalLodPath { get; set; } = new("../GlobalTerrainLod3");
    [Export] public NodePath AnalyticGlobalLodPath { get; set; } = new("../AnalyticGlobalRing");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath CloudLayerPath { get; set; } = new("../RingCloudLayer");
    [Export] public NodePath ScaledFarViewportPath { get; set; } = new("../ScaledFarViewport");
    [Export] public NodePath SmoothTerrainGridPath { get; set; } = new("../SmoothTerrainGrid");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export(PropertyHint.Range, "512,16384,64")] public int WorkingSetLimitMb { get; set; } = 6144;
    [Export(PropertyHint.Range, "1,60,1")] public int RollingWindowSeconds { get; set; } = 10;
    [Export(PropertyHint.Range, "120,7200,60")] public int MaximumFrameSamples { get; set; } = 3600;
    [Export(PropertyHint.Range, "0.1,5.0,0.1")] public float MemoryPollSeconds { get; set; } = 1.0f;
    [ExportGroup("Adaptive Protection")]
    [Export] public bool EnableAdaptiveProtection { get; set; } = true;
    [Export] public bool EnableAdaptiveRenderResolution { get; set; } = false;
    [Export(PropertyHint.Range, "0.5,1.0,0.01")] public float MaximumRenderScale { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0.5,1.0,0.01")] public float WorkingSetFallbackFraction { get; set; } = 0.90f;
    [Export(PropertyHint.Range, "256,4096,64")] public int TerrainMemoryFallbackMb { get; set; } = 1536;
    [Export(PropertyHint.Range, "0.5,1.0,0.05")] public float MinimumRenderScale { get; set; } = 0.70f;
    [Export(PropertyHint.Range, "0.01,0.2,0.01")] public float RenderScaleStep { get; set; } = 0.05f;
    [Export(PropertyHint.Range, "30,240,1")] public float TargetFps { get; set; } = 60.0f;
	[Export(PropertyHint.Range, "1,30,1")] public float RenderScaleRecoverySeconds { get; set; } = 5.0f;
	[Export(PropertyHint.Range, "0.5,1.5,0.05")] public float GpuDownscaleBudgetFraction { get; set; } = 0.90f;
	[Export(PropertyHint.Range, "0.25,1.0,0.05")] public float GpuUpscaleBudgetFraction { get; set; } = 0.75f;
    [ExportGroup("Automated Benchmark")]
    [Export(PropertyHint.Range, "1,30,1")] public float BenchmarkWarmupSeconds { get; set; } = 5.0f;
    [Export(PropertyHint.Range, "5,7200,1")] public float BenchmarkDurationSeconds { get; set; } = 30.0f;
	[Export(PropertyHint.Range, "120,7200,60")] public int MaximumBenchmarkHealthSamples { get; set; } = 1800;
    [Export(PropertyHint.Range, "10,20000,10")] public float BenchmarkTravelSpeedMetersPerSecond { get; set; } = 1200.0f;
	[Export(PropertyHint.Range, "10,300,1")] public float BenchmarkMaximumSettleSeconds { get; set; } = 120.0f;

    private readonly Queue<double> _frameMilliseconds = new();
	private double _frameWindowMilliseconds;
    private RingTerrainLodCoordinator? _coordinator;
    private VoxelChunkGridManager? _grid;
    private RingTerrainGlobalLod? _globalLod;
    private RingAnalyticGlobalLod? _analyticGlobalLod;
    private RingVoxelExplorerController? _explorer;
    private RingworldTechDemo.RingCloudLayer? _cloudLayer;
    private RingScaledFarViewport? _scaledFarViewport;
    private SmoothTerrainGrid? _smoothTerrainGrid;
    private DirectionalLight3D? _sunLight;
    private double _memoryPollElapsed;
    private bool _benchmarkEnabled;
	private bool _benchmarkStandingOnly;
	private bool _benchmarkDisableProxyRendering;
	private bool _benchmarkDisableGlobalRendering;
    private bool _benchmarkSamplingStarted;
	private bool _benchmarkWarmupPositioned;
    private double _benchmarkElapsed;
	private double _benchmarkSamplingStartElapsed;
	private double _benchmarkQuiescentElapsed;
	private double _lastBenchmarkSettleLogElapsed = double.NegativeInfinity;
    private RingCanonicalPosition _highReliefWaypoint;
    private RingCanonicalPosition _lowReliefWaypoint;
    private bool _denseEditBurstApplied;
    private Vector3 _originalSunRotation;
    private double _adaptiveElapsed;
	private double _renderScaleRecoveryElapsed;
    private double _lastMemoryFallbackSeconds = -1000.0;
    private bool _simulateMemoryPressure;
	private long _benchmarkStartWorkingSetBytes;
	private long _privateMemoryBytes;
	private long _benchmarkStartManagedHeapBytes;
	private readonly List<BenchmarkHealthSample> _benchmarkHealthSamples = new();
	private Rid _measuredViewportRid;
	private long _totalAllocatedBytes;
	private int _gen0CollectionCount, _gen1CollectionCount, _gen2CollectionCount, _processThreadCount;
	private string _benchmarkStageLabel = "standing_vista";
	private double _processCpuMilliseconds;

    public long CurrentWorkingSetBytes { get; private set; }
    public long PrivateMemoryBytes => _privateMemoryBytes;
    public long PeakWorkingSetBytes { get; private set; }
    public long ManagedHeapBytes { get; private set; }
    public long EstimatedTerrainBytes { get; private set; }
    public double RollingAverageFps { get; private set; }
    public double RollingOnePercentLowFps { get; private set; }
    public double WorstFrameMilliseconds { get; private set; }
    public bool WorkingSetWithinLimit => CurrentWorkingSetBytes <= (long)WorkingSetLimitMb * 1024L * 1024L;
    public float CurrentRenderScale { get; private set; } = 1.0f;

    public override void _Ready()
    {
        _coordinator = GetNodeOrNull<RingTerrainLodCoordinator>(CoordinatorPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _globalLod = GetNodeOrNull<RingTerrainGlobalLod>(GlobalLodPath);
        _analyticGlobalLod = GetNodeOrNull<RingAnalyticGlobalLod>(AnalyticGlobalLodPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _cloudLayer = GetNodeOrNull<RingworldTechDemo.RingCloudLayer>(CloudLayerPath);
        _scaledFarViewport = GetNodeOrNull<RingScaledFarViewport>(ScaledFarViewportPath);
        _smoothTerrainGrid = GetNodeOrNull<SmoothTerrainGrid>(SmoothTerrainGridPath);
        _sunLight = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        _originalSunRotation = _sunLight?.Rotation ?? Vector3.Zero;
		_measuredViewportRid = GetViewport().GetViewportRid();
		if (_measuredViewportRid.IsValid && EnableAdaptiveProtection)
			RenderingServer.ViewportSetMeasureRenderTime(_measuredViewportRid, true);
        string[] userArguments = OS.GetCmdlineUserArgs();
        _benchmarkEnabled = userArguments.Contains("--certification-benchmark");
		_benchmarkStandingOnly = userArguments.Contains("--benchmark-standing-only");
		_benchmarkDisableProxyRendering = userArguments.Contains("--benchmark-disable-proxy-rendering");
		_benchmarkDisableGlobalRendering = userArguments.Contains("--benchmark-disable-global-rendering");
        _simulateMemoryPressure = userArguments.Contains("--simulate-memory-pressure");
        ApplyBenchmarkArgumentOverrides(userArguments);
        PollMemory();
        if (_benchmarkEnabled)
        {
			if (_measuredViewportRid.IsValid)
				RenderingServer.ViewportSetMeasureRenderTime(_measuredViewportRid, true);
            ResolveReliefWaypoints();
			string mode = _benchmarkStandingOnly ? "standing-only" : "full-route";
            GD.Print($"Certification benchmark armed: mode={mode}, warmup={BenchmarkWarmupSeconds:0}s, route={BenchmarkDurationSeconds:0}s, speed={BenchmarkTravelSpeedMetersPerSecond:0}m/s");
        }
    }

	private void ApplyBenchmarkRenderIsolation()
	{
		if (!_benchmarkEnabled) return;
		if (_benchmarkDisableProxyRendering && _coordinator != null)
		{
			foreach (RingTerrainProxySector level in _coordinator.Levels)
				level.Visible = false;
			GD.Print("Certification benchmark A/B: proxy rendering hidden; streaming remains active.");
		}
		if (_benchmarkDisableGlobalRendering && _globalLod != null)
		{
			_globalLod.Visible = false;
			GD.Print("Certification benchmark A/B: global terrain rendering hidden; building remains active.");
		}
	}

    private void ApplyBenchmarkArgumentOverrides(IEnumerable<string> arguments)
    {
        foreach (string argument in arguments)
        {
            if (TryReadPositiveArgument(argument, "--benchmark-warmup=", out double warmup))
                BenchmarkWarmupSeconds = (float)Math.Clamp(warmup, 0.1, 30.0);
            else if (TryReadPositiveArgument(argument, "--benchmark-duration=", out double duration))
                BenchmarkDurationSeconds = (float)Math.Clamp(duration, 1.0, 7200.0);
            else if (TryReadPositiveArgument(argument, "--benchmark-speed=", out double speed))
                BenchmarkTravelSpeedMetersPerSecond = (float)Math.Clamp(speed, 10.0, 20000.0);
        }
    }

    private static bool TryReadPositiveArgument(string argument, string prefix, out double value)
    {
        value = 0.0;
        return argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && double.TryParse(argument[prefix.Length..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)
            && value > 0.0;
    }

    public override void _Process(double delta)
    {
        double frameMs = Math.Max(0.0, delta * 1000.0);
        _frameMilliseconds.Enqueue(frameMs);
		_frameWindowMilliseconds += frameMs;
		double targetWindowMilliseconds = Math.Max(1000.0, RollingWindowSeconds * 1000.0);
		while (_frameMilliseconds.Count > 1
			&& (_frameWindowMilliseconds - _frameMilliseconds.Peek() >= targetWindowMilliseconds
				|| _frameMilliseconds.Count > MaximumFrameSamples))
		{
			_frameWindowMilliseconds -= _frameMilliseconds.Dequeue();
		}
        WorstFrameMilliseconds = Math.Max(WorstFrameMilliseconds, frameMs);

        if (_benchmarkEnabled) ServiceAutomatedBenchmark(delta);

        _memoryPollElapsed += delta;
        if (_memoryPollElapsed < Math.Max(0.1, MemoryPollSeconds)) return;
        _memoryPollElapsed = 0.0;
        RefreshRollingFrameMetrics();
        PollMemory();
		if (_benchmarkSamplingStarted) RecordBenchmarkHealthSample();
        ServiceAdaptiveProtection();
    }

    private void ServiceAdaptiveProtection()
    {
        if (!EnableAdaptiveProtection || _coordinator == null) return;
        _adaptiveElapsed += Math.Max(0.1, MemoryPollSeconds);
        long workingLimit = (long)WorkingSetLimitMb * 1024L * 1024L;
        long terrainLimit = (long)TerrainMemoryFallbackMb * 1024L * 1024L;
        bool memoryPressure = _simulateMemoryPressure
            || CurrentWorkingSetBytes >= workingLimit * WorkingSetFallbackFraction
            || EstimatedTerrainBytes >= terrainLimit;
        if (memoryPressure && _adaptiveElapsed - _lastMemoryFallbackSeconds >= 30.0)
        {
            if (_coordinator.ApplyMemoryPressureFallback())
                _lastMemoryFallbackSeconds = _adaptiveElapsed;
            _simulateMemoryPressure = false;
        }

        // Memory fallback above remains enabled independently of the player's
        // optional render-resolution automation.
        if (!EnableAdaptiveRenderResolution)
        {
            _renderScaleRecoveryElapsed = 0.0;
            CurrentRenderScale = GetViewport().Scaling3DScale;
            return;
        }

        if (string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase)
            || _frameMilliseconds.Count < 120)
        {
            return;
        }

        Viewport viewport = GetViewport();
        float scale = viewport.Scaling3DScale;
		float ceiling = float.IsFinite(MaximumRenderScale) ? Mathf.Clamp(MaximumRenderScale, 0.01f, 1.0f) : 1.0f;
		float floor = float.IsFinite(MinimumRenderScale) ? Mathf.Min(Mathf.Clamp(MinimumRenderScale, 0.01f, 1.0f), ceiling) : ceiling;
		float step = float.IsFinite(RenderScaleStep) ? Mathf.Max(0.0f, RenderScaleStep) : 0.0f;
		scale = Mathf.Clamp(float.IsFinite(scale) ? scale : ceiling, floor, ceiling);
		double targetFrameMilliseconds = 1000.0 / Math.Max(1.0f, TargetFps);
		double gpuMilliseconds = ReadViewportRenderGpuMilliseconds();
		bool gpuTimingReady = gpuMilliseconds > 0.01;
		bool frameRateLow = RollingAverageFps < TargetFps * 0.92f
			|| RollingOnePercentLowFps < TargetFps * 0.78f;
		bool gpuBound = gpuTimingReady
			&& gpuMilliseconds >= targetFrameMilliseconds * GpuDownscaleBudgetFraction;
		if (frameRateLow && gpuBound)
		{
            scale = Mathf.Max(floor, scale - step);
			_renderScaleRecoveryElapsed = 0.0;
		}
		else
		{
			// V-Sync prevents the old >104% target test from ever succeeding.
			// Recover quality only after sustained cap-level pacing and measured
			// GPU headroom, so CPU/streaming stalls cannot ratchet resolution down.
			bool frameRateRecovered = RollingAverageFps >= TargetFps * 0.97f
				&& RollingOnePercentLowFps >= TargetFps * 0.85f;
			bool gpuHeadroom = gpuTimingReady
				&& gpuMilliseconds <= targetFrameMilliseconds * GpuUpscaleBudgetFraction;
			_renderScaleRecoveryElapsed = scale < ceiling && frameRateRecovered && gpuHeadroom
				? _renderScaleRecoveryElapsed + Math.Max(0.1f, MemoryPollSeconds)
				: 0.0;
			if (_renderScaleRecoveryElapsed >= Math.Max(1.0f, RenderScaleRecoverySeconds))
			{
				scale = Mathf.Min(ceiling, scale + step);
				_renderScaleRecoveryElapsed = 0.0;
			}
		}
        if (!Mathf.IsEqualApprox(scale, viewport.Scaling3DScale)) viewport.Scaling3DScale = scale;
        CurrentRenderScale = viewport.Scaling3DScale;
    }

    private void ServiceAutomatedBenchmark(double delta)
    {
        if (_grid == null || _explorer == null) return;
        _benchmarkElapsed += delta;
		if (!_benchmarkWarmupPositioned)
		{
			ServiceBenchmarkStage(routeTime: 0.0, progress: 0.0);
			_benchmarkWarmupPositioned = true;
		}
        if (_benchmarkElapsed < BenchmarkWarmupSeconds) return;
        if (!_benchmarkSamplingStarted)
        {
			bool quiescent = IsBenchmarkWorldQuiescent();
			_benchmarkQuiescentElapsed = quiescent
				? _benchmarkQuiescentElapsed + Math.Max(0.0, delta)
				: 0.0;
			double settleElapsed = _benchmarkElapsed - BenchmarkWarmupSeconds;
			bool settleTimedOut = settleElapsed >= BenchmarkMaximumSettleSeconds;
			if (_benchmarkQuiescentElapsed < 2.0 && !settleTimedOut)
			{
				if (settleElapsed - _lastBenchmarkSettleLogElapsed >= 10.0)
				{
					_lastBenchmarkSettleLogElapsed = settleElapsed;
					GD.Print($"Certification benchmark waiting for settled residency: workers={_coordinator?.ActiveWorkerCount ?? 0}, uploads={_coordinator?.PendingUploadCount ?? 0}, globalSummary={_globalLod?.GlobalSummaryBuildPending == true}, globalSegments={_globalLod?.InitialGlobalMeshBuildPending == true}.");
				}
				return;
			}
			if (settleTimedOut && _benchmarkQuiescentElapsed < 2.0)
				GD.PushWarning("Certification benchmark settle timeout reached; sampling the remaining background workload.");
            _benchmarkSamplingStarted = true;
			_benchmarkSamplingStartElapsed = _benchmarkElapsed;
			ApplyBenchmarkRenderIsolation();
            _frameMilliseconds.Clear();
			_frameWindowMilliseconds = 0.0;
            WorstFrameMilliseconds = 0.0;
			PollMemory();
			_benchmarkStartWorkingSetBytes = CurrentWorkingSetBytes;
			_benchmarkStartManagedHeapBytes = ManagedHeapBytes;
			_benchmarkHealthSamples.Clear();
			RecordBenchmarkHealthSample();
            GD.Print("Certification benchmark sampling started.");
            GD.Print($"Voxel warmup residency: radius={_grid.StreamingVisualRadiusAround}/{_grid.StreamingVisualRadiusVertical}, desired={_grid.DesiredVisualChunkCount}, loaded={_grid.LoadedChunkCount}, pooled={_grid.PooledChunkCount}, virtualAir={_grid.KnownEmptyStreamingChunkCount}, virtualSolid={_grid.KnownSolidStreamingChunkCount}, queued={_grid.PendingChunkLoadCount}, deferred={_grid.DeferredVoxelMeshCount}, meshActive={_grid.ActiveVoxelMeshWorkerCount}, meshReady={_grid.CompletedVoxelMeshCount}, prepareMax={_grid.MaxVoxelMeshPrepareMilliseconds:0.00}ms, neighborMax={_grid.MaxVoxelNeighborCaptureMilliseconds:0.00}ms, workerMax={_grid.MaxVoxelMeshWorkerMilliseconds:0.00}ms, uploadMax={_grid.MaxVoxelMeshUploadMilliseconds:0.00}ms, stale={_grid.DiscardedStaleVoxelMeshCount}, data={_grid.EstimatedVoxelDataBytes / (1024.0 * 1024.0):0.0}MiB");
        }

		double routeTime = _benchmarkElapsed - _benchmarkSamplingStartElapsed;
        double progress = Math.Clamp(routeTime / BenchmarkDurationSeconds, 0.0, 1.0);
        ServiceBenchmarkStage(routeTime, progress);

        if (routeTime < BenchmarkDurationSeconds) return;
        _benchmarkEnabled = false;
        RefreshRollingFrameMetrics();
        PollMemory();
		RecordBenchmarkHealthSample();
        string reportPath = WriteBenchmarkReport();
        GD.Print($"Certification benchmark complete: avg={RollingAverageFps:0.0}fps, 1%low={RollingOnePercentLowFps:0.0}fps, worst={WorstFrameMilliseconds:0.0}ms, workingSet={CurrentWorkingSetBytes / (1024.0 * 1024.0):0.0}MiB, report={reportPath}");
        GD.Print($"Voxel residency: radius={_grid.StreamingVisualRadiusAround}/{_grid.StreamingVisualRadiusVertical}, desired={_grid.DesiredVisualChunkCount}, loaded={_grid.LoadedChunkCount}, pooled={_grid.PooledChunkCount}, virtualAir={_grid.KnownEmptyStreamingChunkCount}, virtualSolid={_grid.KnownSolidStreamingChunkCount}, queued={_grid.PendingChunkLoadCount}, deferred={_grid.DeferredVoxelMeshCount}, meshActive={_grid.ActiveVoxelMeshWorkerCount}, meshReady={_grid.CompletedVoxelMeshCount}, prepareMax={_grid.MaxVoxelMeshPrepareMilliseconds:0.00}ms, neighborMax={_grid.MaxVoxelNeighborCaptureMilliseconds:0.00}ms, workerMax={_grid.MaxVoxelMeshWorkerMilliseconds:0.00}ms, uploadMax={_grid.MaxVoxelMeshUploadMilliseconds:0.00}ms, stale={_grid.DiscardedStaleVoxelMeshCount}, data={_grid.EstimatedVoxelDataBytes / (1024.0 * 1024.0):0.0}MiB");
        GetTree().Quit();
    }

    private void ServiceBenchmarkStage(double routeTime, double progress)
    {
        if (_grid == null || _explorer == null) return;
		if (_benchmarkStandingOnly)
		{
			_benchmarkStageLabel = "standing_vista";
			_cloudLayer?.SetBenchmarkCloudLoad(false);
			_scaledFarViewport?.SetBenchmarkCloudLoad(false);
			if (_sunLight != null) _sunLight.Rotation = _originalSunRotation;
			_explorer.SetBenchmarkCanonicalPosition(_highReliefWaypoint);
			return;
		}
		_cloudLayer?.SetBenchmarkCloudLoad(progress >= 0.74 && progress < 0.86);
		_scaledFarViewport?.SetBenchmarkCloudLoad(progress >= 0.74 && progress < 0.86);
		if (_sunLight != null)
			_sunLight.Rotation = progress >= 0.86
                ? new Vector3(Mathf.DegToRad(-2.0f), Mathf.DegToRad(88.0f), 0.0f)
                : _originalSunRotation;

		RingCanonicalPosition position;
		float simulatedSpeed = 0.0f;
		bool simulatedNoclip = false;
		if (progress < 0.12)
		{
			_benchmarkStageLabel = "standing_vista";
			position = _highReliefWaypoint;
		}
		else if (progress < 0.32)
		{
			_benchmarkStageLabel = "high_speed_seam";
            double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
			double stageTime = routeTime - BenchmarkDurationSeconds * 0.12;
            double around = circumference - 5000.0 + stageTime * BenchmarkTravelSpeedMetersPerSecond;
            double across = _grid.RingAcrossWidthMeters * 0.5
                + Math.Sin(stageTime * 0.31) * _grid.RingAcrossWidthMeters * 0.4;
            int surface = _grid.TerrainSampler?.GetSurfaceHeight((int)Math.Round(around), (int)Math.Round(across)) ?? 0;
			position = new RingCanonicalPosition(around, surface + 300.0, across);
			simulatedSpeed = BenchmarkTravelSpeedMetersPerSecond;
			simulatedNoclip = true;
        }
		else if (progress < 0.42)
		{
			_benchmarkStageLabel = "maximum_relief";
			position = ((int)(routeTime * 2.0) & 1) == 0 ? _highReliefWaypoint : _lowReliefWaypoint;
			simulatedSpeed = BenchmarkTravelSpeedMetersPerSecond;
			simulatedNoclip = true;
		}
		else if (progress < 0.62)
		{
			_benchmarkStageLabel = "cave_descent_reascent";
			double caveStageTime = routeTime - BenchmarkDurationSeconds * 0.42;
			double caveTime = (progress - 0.42) / 0.20;
			// Two full surface-to-cave-to-surface cycles: +120m at each
			// endpoint and -720m at both cave extrema.
			double verticalOffset = 120.0 - (0.5 - 0.5 * Math.Cos(caveTime * Math.Tau * 2.0)) * 840.0;
			int surface = _grid.TerrainSampler?.GetSurfaceHeight(
				(int)Math.Round(_highReliefWaypoint.AroundMeters),
				(int)Math.Round(_highReliefWaypoint.AcrossMeters)) ?? 0;
			position = new RingCanonicalPosition(
				_highReliefWaypoint.AroundMeters,
				surface + verticalOffset,
				_highReliefWaypoint.AcrossMeters);
			// The suspension policy intentionally stays active until the player
			// stops. Give it a deterministic stopped hold at the stage boundary,
			// then keep cave motion below the entry threshold so all cave tiers
			// remain exercised for the rest of this stage.
			double resumeHoldSeconds = (_smoothTerrainGrid?.FastTravelResumeStoppedSeconds ?? 0.25f) + 0.25;
			simulatedSpeed = caveStageTime < resumeHoldSeconds
				? 0.0f
				: Math.Min(
					BenchmarkTravelSpeedMetersPerSecond,
					Math.Max(10.0f, (_smoothTerrainGrid?.FastTravelSpeedThresholdMetersPerSecond ?? 750.0f) - 1.0f));
			simulatedNoclip = true;
		}
		else if (progress < 0.74)
		{
			_benchmarkStageLabel = "smooth_edit_burst";
			if (!_denseEditBurstApplied) ApplyDenseEditBurst();
			position = _highReliefWaypoint;
		}
		else if (progress < 0.86)
		{
			_benchmarkStageLabel = "cloud_heavy";
			position = _highReliefWaypoint;
		}
		else
		{
			_benchmarkStageLabel = "worst_lighting";
			double sweep = (progress - 0.86) / 0.14;
            position = new RingCanonicalPosition(
                _highReliefWaypoint.AroundMeters + sweep * 25000.0,
				_highReliefWaypoint.VerticalMeters + 500.0,
				_highReliefWaypoint.AcrossMeters);
			simulatedSpeed = BenchmarkTravelSpeedMetersPerSecond;
			simulatedNoclip = true;
        }
        _explorer.SetBenchmarkCanonicalPosition(position, simulatedSpeed, simulatedNoclip);
    }

    private void ResolveReliefWaypoints()
    {
        if (_grid?.TerrainSampler == null) return;
        int sampleCount = 256;
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        int across = Mathf.RoundToInt(_grid.RingAcrossWidthMeters * 0.5f);
        int highest = int.MinValue;
        int lowest = int.MaxValue;
        int highestAround = 0;
        int lowestAround = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            int around = (int)Math.Round(i * circumference / sampleCount);
            int height = _grid.TerrainSampler.GetSurfaceHeight(around, across);
            if (height > highest) { highest = height; highestAround = around; }
            if (height < lowest) { lowest = height; lowestAround = around; }
        }
        _highReliefWaypoint = new RingCanonicalPosition(highestAround, highest + 120.0, across);
        _lowReliefWaypoint = new RingCanonicalPosition(lowestAround, lowest + 120.0, across);
    }

    private void ApplyDenseEditBurst()
    {
		if (_grid?.TerrainSampler == null || _smoothTerrainGrid == null) return;
		_denseEditBurstApplied = true;
		int applied = 0;
		int originAround = (int)Math.Round(_highReliefWaypoint.AroundMeters);
		int originAcross = (int)Math.Round(_highReliefWaypoint.AcrossMeters);
		for (int z = 0; z < 4; z++)
		for (int x = 0; x < 4; x++)
		{
			int around = originAround + x * 4;
			int across = originAcross + z * 4;
			int surface = _grid.TerrainSampler.GetSurfaceHeight(around, across);
			Vector3 world = _grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(around, surface, across));
			if (_smoothTerrainGrid.TryApplyNaturalBrushAtWorldPosition(
				world, addsMatter: false, radius: 2.0f, material: SmoothTerrainMaterial.Soil, command: out _)) applied++;
		}
		GD.Print($"Certification smooth-edit stage: applied={applied}/16");
    }

    private string WriteBenchmarkReport()
    {
        bool headless = string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase);
        SmoothTerrainGrid.ResourceDiagnostics smooth = _smoothTerrainGrid?.CaptureResourceDiagnostics() ?? default;
		SmoothTerrainGrid.CaveLodTelemetry cave = _smoothTerrainGrid?.CaptureCaveLodTelemetry() ?? default;
        bool performancePass = RollingOnePercentLowFps >= 45.0
            && CurrentWorkingSetBytes <= (long)WorkingSetLimitMb * 1024L * 1024L;
        var report = new
        {
            schema_version = 1,
            generated_utc = DateTime.UtcNow.ToString("O"),
            renderer = RenderingServer.GetVideoAdapterName(),
            display_server = DisplayServer.GetName(),
            headless,
            valid_hardware_certification = !headless,
            preset = _coordinator?.CurrentPresetName ?? "unavailable",
            ring = new
            {
                radius_m = _grid?.GetCanonicalRingCircumferenceMeters64() / Math.Tau,
                circumference_m = _grid?.GetCanonicalRingCircumferenceMeters64(),
                width_m = _grid?.RingAcrossWidthMeters,
                terrain_floor_m = _grid?.MinExcavationGlobalY,
                terrain_ceiling_m = _grid?.MaxBuildGlobalY,
                structural_depth_m = _grid?.StructuralDepthMeters
            },
            route = new
            {
				mode = _benchmarkStandingOnly ? "standing_only" : "full_route",
				disable_proxy_rendering = _benchmarkDisableProxyRendering,
				disable_global_rendering = _benchmarkDisableGlobalRendering,
                warmup_seconds = BenchmarkWarmupSeconds,
                duration_seconds = BenchmarkDurationSeconds,
                speed_mps = BenchmarkTravelSpeedMetersPerSecond,
				stages = _benchmarkStandingOnly
					? new[] { "standing_vista" }
					: new[] { "standing_vista", "high_speed_seam", "maximum_relief", "cave_descent_reascent", "smooth_edit_burst", "cloud_heavy", "worst_lighting" }
            },
            voxels = new
            {
                surface_radius_chunks = _grid?.StreamingVisualRadiusAround,
                vertical_radius_chunks = _grid?.StreamingVisualRadiusVertical,
                collision_surface_radius_chunks = _grid?.StreamingCollisionRadiusAround,
                collision_vertical_radius_chunks = _grid?.StreamingCollisionRadiusVertical,
                desired_chunks = _grid?.DesiredVisualChunkCount,
                loaded_chunks = _grid?.LoadedChunkCount,
                pooled_chunks = _grid?.PooledChunkCount,
                virtual_air_chunks = _grid?.KnownEmptyStreamingChunkCount,
                virtual_solid_chunks = _grid?.KnownSolidStreamingChunkCount,
                pending_chunks = _grid?.PendingChunkLoadCount,
                active_mesh_workers = _grid?.ActiveVoxelMeshWorkerCount,
                completed_meshes = _grid?.CompletedVoxelMeshCount,
                deferred_meshes = _grid?.DeferredVoxelMeshCount,
                max_mesh_prepare_ms = _grid?.MaxVoxelMeshPrepareMilliseconds,
                max_neighbor_capture_ms = _grid?.MaxVoxelNeighborCaptureMilliseconds,
                max_mesh_worker_ms = _grid?.MaxVoxelMeshWorkerMilliseconds,
                max_mesh_upload_ms = _grid?.MaxVoxelMeshUploadMilliseconds,
                discarded_stale_meshes = _grid?.DiscardedStaleVoxelMeshCount,
                estimated_block_data_bytes = _grid?.EstimatedVoxelDataBytes
            },
			smooth_terrain = new
			{
				telemetry = cave,
				pooled_presenter_count = smooth.PooledPresenterCount,
				auxiliary_resident_entry_count = smooth.AuxiliaryResidentEntryCount,
				auxiliary_ready_result_count = smooth.AuxiliaryReadyResultCount,
				tracked_task_count = smooth.TrackedTaskCount,
				tracked_presenter_count = smooth.TrackedPresenterCount,
				estimated_resident_bytes = smooth.EstimatedResidentBytes,
				base_tier = new { active_presenters = smooth.BaseActivePresenterCount, pooled_presenters = smooth.BasePooledPresenterCount },
				near_transition_tier = new { active_presenters = smooth.NearTransitionActivePresenterCount, pooled_presenters = smooth.NearTransitionPooledPresenterCount },
				medium_tier = new { active_presenters = smooth.MediumActivePresenterCount, pooled_presenters = smooth.MediumPooledPresenterCount },
				bridge_4m_tier = new { active_presenters = smooth.Bridge4ActivePresenterCount, pooled_presenters = smooth.Bridge4PooledPresenterCount },
				bridge_8m_tier = new { active_presenters = smooth.Bridge8ActivePresenterCount, pooled_presenters = smooth.Bridge8PooledPresenterCount },
				bridge_16m_tier = new { active_presenters = smooth.Bridge16ActivePresenterCount, pooled_presenters = smooth.Bridge16PooledPresenterCount },
				bridge_32m_tier = new { active_presenters = smooth.Bridge32ActivePresenterCount, pooled_presenters = smooth.Bridge32PooledPresenterCount },
				coarse_tier = new { active_presenters = smooth.CoarseActivePresenterCount, pooled_presenters = smooth.CoarsePooledPresenterCount }
			},
			proxy_levels = (_coordinator?.Levels ?? Array.Empty<RingTerrainProxySector>()).Select(level => new
			{
				lod_label = level.LodLabel,
				active_sector_count = level.ActiveSectorCount,
				warm_sector_count = level.WarmSectorCount,
				pooled_sector_count = level.PooledSectorCount,
				created_sector_node_count = level.CreatedSectorNodeCount,
				rebuilt_sector_mesh_count = level.RebuiltSectorMeshCount,
				reused_sector_node_count = level.ReusedSectorNodeCount
			}).ToArray(),
            performance = new
            {
                rolling_average_fps = RollingAverageFps,
                rolling_one_percent_low_fps = RollingOnePercentLowFps,
                worst_frame_ms = WorstFrameMilliseconds,
				current_render_scale = CurrentRenderScale,
				scene_node_count = GetTree().GetNodeCount(),
				engine_process_ms = ReadPerformanceMonitor(Performance.Monitor.TimeProcess) * 1000.0,
				physics_process_ms = ReadPerformanceMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0,
				render_setup_cpu_ms = RenderingServer.GetFrameSetupTimeCpu(),
				viewport_render_cpu_ms = ReadViewportRenderCpuMilliseconds(),
				viewport_render_gpu_ms = ReadViewportRenderGpuMilliseconds(),
				rendered_objects = (int)ReadPerformanceMonitor(Performance.Monitor.RenderTotalObjectsInFrame),
				rendered_primitives = (long)ReadPerformanceMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame),
				draw_calls = (int)ReadPerformanceMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
				resource_count = (int)ReadPerformanceMonitor(Performance.Monitor.ObjectResourceCount),
				orphan_node_count = (int)ReadPerformanceMonitor(Performance.Monitor.ObjectOrphanNodeCount),
				video_memory_bytes = (long)ReadPerformanceMonitor(Performance.Monitor.RenderVideoMemUsed),
                working_set_bytes = CurrentWorkingSetBytes,
				private_memory_bytes = _privateMemoryBytes,
				working_set_growth_bytes = CurrentWorkingSetBytes - _benchmarkStartWorkingSetBytes,
                peak_working_set_bytes = PeakWorkingSetBytes,
                working_set_limit_bytes = (long)WorkingSetLimitMb * 1024L * 1024L,
                managed_heap_bytes = ManagedHeapBytes,
				managed_heap_growth_bytes = ManagedHeapBytes - _benchmarkStartManagedHeapBytes,
				total_allocated_bytes = _totalAllocatedBytes,
				gen0_collection_count = _gen0CollectionCount,
				gen1_collection_count = _gen1CollectionCount,
				gen2_collection_count = _gen2CollectionCount,
				process_cpu_ms = _processCpuMilliseconds,
				process_thread_count = _processThreadCount,
                estimated_terrain_bytes = EstimatedTerrainBytes,
				health_samples = _benchmarkHealthSamples,
                proxy_upload_max_ms = _coordinator?.Levels.Count > 0
                    ? _coordinator.Levels.Max(level => level.MaxStreamingBuildMilliseconds)
                    : 0.0,
                performance_thresholds_pass = performancePass
            }
        };

		string directory = ResolveBenchmarkReportDirectory();
        string path = Path.Combine(directory, $"ring_certification_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private void RefreshRollingFrameMetrics()
    {
        if (_frameMilliseconds.Count == 0) return;
        double[] samples = _frameMilliseconds.ToArray();
        double averageMs = samples.Average();
        RollingAverageFps = averageMs > 0.0 ? 1000.0 / averageMs : 0.0;
        Array.Sort(samples);
        int slowIndex = Math.Clamp((int)Math.Ceiling(samples.Length * 0.99) - 1, 0, samples.Length - 1);
        double slowFrameMs = samples[slowIndex];
        RollingOnePercentLowFps = slowFrameMs > 0.0 ? 1000.0 / slowFrameMs : 0.0;
    }

    private void PollMemory()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        CurrentWorkingSetBytes = process.WorkingSet64;
		_privateMemoryBytes = process.PrivateMemorySize64;
        PeakWorkingSetBytes = Math.Max(PeakWorkingSetBytes, CurrentWorkingSetBytes);
        ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
		_totalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
		_gen0CollectionCount = GC.CollectionCount(0);
		_gen1CollectionCount = GC.CollectionCount(1);
		_gen2CollectionCount = GC.CollectionCount(2);
		_processCpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds;
		_processThreadCount = process.Threads.Count;
		CurrentRenderScale = GetViewport().Scaling3DScale;
        EstimatedTerrainBytes = (_coordinator?.EstimatedActiveMeshBytes ?? 0L)
            + (_coordinator?.EstimatedPendingMeshBytes ?? 0L)
            + (_grid?.EstimatedVoxelDataBytes ?? 0L)
            + (_globalLod?.EstimatedSummaryBytes ?? 0L)
            + (_globalLod?.EstimatedGlobalMeshBytes ?? 0L)
            + (_analyticGlobalLod?.EstimatedMeshBytes ?? 0L);
    }

	private void RecordBenchmarkHealthSample()
	{
		double elapsed = Math.Max(0.0, _benchmarkElapsed - _benchmarkSamplingStartElapsed);
		if (_benchmarkHealthSamples.Count > 0
			&& elapsed - _benchmarkHealthSamples[^1].ElapsedSeconds < 0.25)
			return;
		IReadOnlyList<RingTerrainProxySector>? levels = _coordinator?.Levels;
		SmoothTerrainGrid.ResourceDiagnostics smooth = _smoothTerrainGrid?.CaptureResourceDiagnostics() ?? default;
		SmoothTerrainGrid.CaveLodTelemetry cave = _smoothTerrainGrid?.CaptureCaveLodTelemetry() ?? default;
		int maxSamples = Math.Max(120, MaximumBenchmarkHealthSamples);
		if (_benchmarkHealthSamples.Count >= maxSamples)
		{
			// Preserve the baseline sample and an evenly spaced recent trend.
			int removeIndex = _benchmarkHealthSamples.Count > 1 ? 1 : 0;
			_benchmarkHealthSamples.RemoveAt(removeIndex);
		}
		_benchmarkHealthSamples.Add(new BenchmarkHealthSample(
			_benchmarkStageLabel,
			elapsed,
			RollingAverageFps,
			RollingOnePercentLowFps,
			CurrentWorkingSetBytes,
			_privateMemoryBytes,
			ManagedHeapBytes,
			EstimatedTerrainBytes,
			smooth.PooledPresenterCount,
			smooth.AuxiliaryResidentEntryCount,
			smooth.AuxiliaryReadyResultCount,
			smooth.TrackedTaskCount,
			smooth.TrackedPresenterCount,
			smooth.EstimatedResidentBytes,
			smooth.BaseActivePresenterCount,
			smooth.BasePooledPresenterCount,
			smooth.NearTransitionActivePresenterCount,
			smooth.NearTransitionPooledPresenterCount,
			smooth.MediumActivePresenterCount,
			smooth.MediumPooledPresenterCount,
			smooth.Bridge4ActivePresenterCount,
			smooth.Bridge4PooledPresenterCount,
			smooth.Bridge8ActivePresenterCount,
			smooth.Bridge8PooledPresenterCount,
			smooth.Bridge16ActivePresenterCount,
			smooth.Bridge16PooledPresenterCount,
			smooth.Bridge32ActivePresenterCount,
			smooth.Bridge32PooledPresenterCount,
			smooth.CoarseActivePresenterCount,
			smooth.CoarsePooledPresenterCount,
			_smoothTerrainGrid?.FastTravelFocusSuspended ?? false,
			_smoothTerrainGrid?.FastTravelDeferredRefreshCount ?? 0UL,
			_smoothTerrainGrid?.FastTravelResumeRefreshCount ?? 0UL,
			cave.Base.Visible+cave.NearTransition.Visible+cave.Medium.Visible+cave.Bridge4.Visible+cave.Bridge8.Visible+cave.Bridge16.Visible+cave.Bridge32.Visible+cave.Coarse.Visible,
			cave.Base.Ready+cave.NearTransition.Ready+cave.Medium.Ready+cave.Bridge4.Ready+cave.Bridge8.Ready+cave.Bridge16.Ready+cave.Bridge32.Ready+cave.Coarse.Ready,
			cave.CancellationRequests,
			cave.LastInstallMilliseconds,
			cave.BudgetExceededFrames,
			cave.QueuedReadyBytes,
			cave.TotalEstimatedBytes,
			cave.PeakEstimatedBytes,
			cave.DeferredQueueDepth,
			cave.ActiveWorkers,
			_totalAllocatedBytes,
			_gen0CollectionCount,
			_gen1CollectionCount,
			_gen2CollectionCount,
			_processCpuMilliseconds,
			_processThreadCount,
			CurrentRenderScale,
			GetTree().GetNodeCount(),
			levels?.Sum(level => level.ActiveSectorCount) ?? 0,
			levels?.Sum(level => level.WarmSectorCount) ?? 0,
			levels?.Sum(level => level.PooledSectorCount) ?? 0,
			levels?.Sum(level => level.CreatedSectorNodeCount) ?? 0,
			levels?.Sum(level => level.RebuiltSectorMeshCount) ?? 0,
			_coordinator?.ActiveWorkerCount ?? 0,
			_coordinator?.PendingUploadCount ?? 0,
			_grid?.LoadedChunkCount ?? 0,
			_grid?.PooledChunkCount ?? 0,
			ReadPerformanceMonitor(Performance.Monitor.TimeProcess) * 1000.0,
			ReadPerformanceMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0,
			RenderingServer.GetFrameSetupTimeCpu(),
			ReadViewportRenderCpuMilliseconds(),
			ReadViewportRenderGpuMilliseconds(),
			(int)ReadPerformanceMonitor(Performance.Monitor.RenderTotalObjectsInFrame),
			(long)ReadPerformanceMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame),
			(int)ReadPerformanceMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
			(int)ReadPerformanceMonitor(Performance.Monitor.ObjectResourceCount),
			(int)ReadPerformanceMonitor(Performance.Monitor.ObjectOrphanNodeCount),
			(long)ReadPerformanceMonitor(Performance.Monitor.RenderVideoMemUsed)));
	}

	private static double ReadPerformanceMonitor(Performance.Monitor monitor) =>
		Performance.GetMonitor(monitor);

	private double ReadViewportRenderCpuMilliseconds() => _measuredViewportRid.IsValid
		? RenderingServer.ViewportGetMeasuredRenderTimeCpu(_measuredViewportRid)
		: 0.0;

	private double ReadViewportRenderGpuMilliseconds() => _measuredViewportRid.IsValid
		? RenderingServer.ViewportGetMeasuredRenderTimeGpu(_measuredViewportRid)
		: 0.0;

	private bool IsBenchmarkWorldQuiescent()
	{
		return (_coordinator?.ActiveWorkerCount ?? 0) == 0
			&& (_coordinator?.PendingUploadCount ?? 0) == 0
			&& (_grid?.PendingChunkLoadCount ?? 0) == 0
			&& (_grid?.PendingChunkRebuildCount ?? 0) == 0
			&& (_grid?.ActiveVoxelMeshWorkerCount ?? 0) == 0
			&& (_grid?.CompletedVoxelMeshCount ?? 0) == 0
			&& (_grid?.DeferredVoxelMeshCount ?? 0) == 0
			&& (_smoothTerrainGrid?.IsQuiescent ?? true)
			&& _globalLod?.GlobalSummaryBuildPending != true
			&& _globalLod?.InitialGlobalMeshBuildPending != true;
	}

	private static string ResolveBenchmarkReportDirectory()
	{
		return RingGeneratedOutputPaths.Directory("reports/certification");
	}
}
