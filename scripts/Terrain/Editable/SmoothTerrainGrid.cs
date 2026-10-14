	 using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Underground;

namespace RingworldTechDemo.Terrain.Editable;

/// <summary>Opt-in Stage-2 streamer. It is deliberately unattached to live scenes and legacy terrain.</summary>
public partial class SmoothTerrainGrid : Node3D, ICaveLodProfileTarget
{
	[Export] public NodePath SourceGridPath { get; set; } = new();
	[Export] public int RadiusAround { get; set; } = 1;
	[Export] public int RadiusAcross { get; set; } = 1;
	[Export] public int RadiusVertical { get; set; } = 0;
	[Export] public int CollisionRadius { get; set; } = 1;
	[Export] public bool EnableFocusCenteredCollisionCoverage { get; set; } = false;
	[ExportGroup("Near volumetric visual coverage")]
	[Export] public bool EnableFocusCenteredVisualCoverage { get; set; } = false;
	[Export(PropertyHint.Range,"0,8,1")] public int FocusVisualRadiusAround { get; set; } = 1;
	[Export(PropertyHint.Range,"0,8,1")] public int FocusVisualRadiusAcross { get; set; } = 1;
	[Export(PropertyHint.Range,"0,8,1")] public int FocusVisualRadiusVertical { get; set; } = 1;
	[ExportGroup("Medium volumetric visual coverage")]
	[Export] public bool EnableMediumVolumetricVisualCoverage { get; set; } = false;
	[Export(PropertyHint.Range,"0,12,1")] public int MediumVisualRadiusAround { get; set; } = 2;
	[Export(PropertyHint.Range,"0,12,1")] public int MediumVisualRadiusAcross { get; set; } = 2;
	[Export(PropertyHint.Range,"0,12,1")] public int MediumVisualRadiusVertical { get; set; } = 1;
	[ExportGroup("Coarse volumetric visual coverage")]
	[Export] public bool EnableCoarseVolumetricVisualCoverage { get; set; } = false;
	[Export(PropertyHint.Range,"0,4,1")] public int CoarseVisualRadiusAround { get; set; } = 1;
	[Export(PropertyHint.Range,"0,4,1")] public int CoarseVisualRadiusAcross { get; set; } = 1;
	[Export(PropertyHint.Range,"0,4,1")] public int CoarseVisualRadiusVertical { get; set; } = 1;
	[Export(PropertyHint.Range,"32,4096,32")] public float CoarseUndergroundActivationDepthMeters { get; set; } = 256.0f;
	/// <summary>Ready coarse sectors within this many sectors outside the wanted ring stay resident (hidden) so a brief shrink does not rebuild them.</summary>
	[Export(PropertyHint.Range,"0,4,1")] public int CoarseRetainMarginSectors { get; set; } = 1;
	/// <summary>Distance above the activation depth the focus must rise before the coarse underground state releases.</summary>
	[Export(PropertyHint.Range,"0,1024,8")] public float CoarseActivationHysteresisMeters { get; set; } = 64.0f;
	[ExportGroup("Cave LOD bridge-shell coverage")]
	[Export] public bool EnableVolumetricBridgeShells { get; set; } = false;
	/// <summary>
	/// During an underground stitched-cave load, admit the collision/edit
	/// bubble first and the coarse visual shell second. Remaining fine and
	/// bridge work keeps the bounded fair scheduler; no tier is disabled.
	/// </summary>
	[Export] public bool EnableCavePreloadScheduling { get; set; } = true;
	/// <summary>Automatic worker count: half the logical cores minus two for the main thread, render and physics, bounded to 2..8. 16 workers were ~3.5x faster on cave views but starved the main thread (35 ms frames).</summary>
	public static int DefaultMaxWorkers => Math.Clamp(System.Environment.ProcessorCount / 2 - 2, 2, 8);
	/// <summary>Concurrent mesh/lattice workers. Changes made while running are applied by <see cref="ApplyWorkerCountChange"/> on the next frame.</summary>
	[Export(PropertyHint.Range, "1,64,1")] public int MaxWorkers { get; set; } = DefaultMaxWorkers;
	/// <summary>Diagnostic escape hatch for controlled sampler contention benchmarks.</summary>
	internal bool UseIndependentWorkerSamplers { get; set; } = true;
	[Export(PropertyHint.Range, "1,256,1")] public int MaxOutstandingMeshWork { get; set; } = 32;
	/// <summary>Safety cap on mesh installs per frame across all tiers; InstallBudgetMilliseconds is the normal limiter. Results with no mesh (uniform/empty) do not count.</summary>
	[Export(PropertyHint.Range, "1,64,1")] public int MaxInstallsPerFrame { get; set; } = 8;
	/// <summary>Main-thread time budget shared by every tier's installs in one frame; at least one install always proceeds.</summary>
	[Export(PropertyHint.Range, "0.25,16,0.25")] public float InstallBudgetMilliseconds { get; set; } = 1.5f;
	[Export] public NodePath StreamingFocusPath { get; set; } = new();
	[Export] public float StreamingUpdateIntervalSeconds { get; set; } = .1f;
	[ExportGroup("Fast travel streaming")]
	// Local terrain remains authoritative while noclipping; do not leave a
	// stale focus window behind merely because movement is fast.
	[Export] public bool SuspendFocusRebuildsDuringFastTravel { get; set; }
	[Export(PropertyHint.Range, "100,1000000,100")] public float FastTravelSpeedThresholdMetersPerSecond { get; set; } = 750.0f;
	[Export(PropertyHint.Range, "0.0,100.0,0.1")] public float FastTravelStoppedSpeedThresholdMetersPerSecond { get; set; } = 1.0f;
	[Export(PropertyHint.Range, "0.0,2.0,0.05")] public float FastTravelResumeStoppedSeconds { get; set; } = 0.25f;
	[ExportGroup("Altitude-aware residency")]
	[Export] public bool EnableAltitudeAwareResidency { get; set; } = true;
	[Export(PropertyHint.Range,"32,10000,32")] public float SurfaceResidencyEnterAltitudeMeters { get; set; } = 192.0f;
	[Export(PropertyHint.Range,"32,10000,32")] public float SurfaceResidencyExitAltitudeMeters { get; set; } = 384.0f;
	[Export] public bool EnableHotChunkPatches { get; set; } = false;
	[Export(PropertyHint.Range,"0,16,.25")] public float HotChunkHysteresisMeters { get; set; } = 4.0f;
	[Export(PropertyHint.Range,"1,30,.5")] public float HotChunkDemotionIdleSeconds { get; set; } = 6.0f;
	private readonly Dictionary<SmoothTerrainChunkCoordinate, Entry> _entries = new();
	// These three sets deliberately describe different owners.  They are only
	// unioned at scheduling time: sharing a resident parent must never imply
	// that a visual-ready result is also collision or edit-subdivision ready.
	private readonly HashSet<SmoothTerrainChunkCoordinate> _surfaceVisualDesired = new();
	private readonly HashSet<SmoothTerrainChunkCoordinate> _nearVolumetricVisualDesired = new();
	// Transvoxel faces are deliberately a separate render-only presentation.
	// The regular near entry retains its collision and hot-patch lifetime.
	private readonly HashSet<SmoothTerrainChunkCoordinate> _nearTransitionDesired = new();
	private readonly Dictionary<SmoothTerrainChunkCoordinate, NearTransitionEntry> _nearTransitionEntries = new();
	private readonly Queue<SmoothTerrainPresenter> _nearTransitionPool = new();
	private readonly ConcurrentQueue<NearTransitionResult> _nearTransitionReady = new();
	// Medium sectors deliberately live outside the 32 m near-parent entry
	// table: they are 64 m, 2 m-sample render-only representations.
	private readonly HashSet<SmoothTerrainChunkCoordinate> _mediumVolumetricVisualDesired = new();
	private readonly Dictionary<SmoothTerrainChunkCoordinate, MediumEntry> _mediumEntries = new();
	private readonly Queue<SmoothTerrainPresenter> _mediumPool = new();
	private readonly ConcurrentQueue<MediumResult> _mediumReady = new();
	// 2 km sectors remain a strictly render-only representation. Keeping
	// their lifetime separate prevents a megacavern shell becoming physics or
	// edit ownership by accident.
	private readonly HashSet<SmoothTerrainChunkCoordinate> _coarseVolumetricVisualDesired = new();
	private readonly Dictionary<SmoothTerrainChunkCoordinate, CoarseEntry> _coarseEntries = new();
	private readonly Queue<SmoothTerrainPresenter> _coarsePool = new();
	private readonly ConcurrentQueue<CoarseResult> _coarseReady = new();
	// Four render-only shells fill the resolution gap from 2 m to 64 m. They
	// share one generic lifecycle rather than four subtly divergent streamers.
	private readonly BridgeTier[] _bridgeTiers = { new(4), new(8), new(16), new(32) };
	// Render union only. Surface-proxy handoff must query _surfaceVisualDesired,
	// never this union, because a deep near cave mesh is not surface coverage.
	private readonly HashSet<SmoothTerrainChunkCoordinate> _visualDesired = new();
	private readonly HashSet<SmoothTerrainChunkCoordinate> _collisionDesired = new();
	private readonly HashSet<SmoothTerrainChunkCoordinate> _editSubdivisionDesired = new();
	private readonly HashSet<SmoothTerrainChunkCoordinate> _residentBuildDesired = new();
	private readonly Queue<SmoothTerrainPresenter> _pool = new();
	private readonly Queue<SmoothTerrainPresenter> _physicsDeferredReleases = new();
	private readonly ConcurrentQueue<Result> _ready = new();
	private readonly ConcurrentDictionary<Task, byte> _tasks = new();
	private readonly object _lifetimeGate = new();
	private readonly List<Task> _trackedTasks = new();
	// Task.Run itself is work: allowing every desired sector to wait on the
	// worker semaphore creates an unbounded allocation/cancellation backlog.
	// These FIFO queues bound that backlog and the rotating dispatcher ensures
	// a busy near tier cannot starve the cave presentation tiers.
	private readonly Queue<DeferredMeshWork>[] _deferredWork =
	{
		new(), new(), new(), new(), new(), new()
	};
	private int _nextDeferredTier;
	private ulong _deferredSubmitted;
	private ulong _deferredCancelled;
	private readonly ConcurrentDictionary<SmoothTerrainChunkCoordinate, DiagnosticRecord> _diagnostics = new();
	private bool _stopping;
	private readonly List<SmoothTerrainEditCommand> _commands = new();
	// A resident coarse LOD entry may legitimately retain an older global
	// journal identity when none of the subsequent local stamps overlap its
	// capture volume. Keep the revision-to-stamp history so deferred refreshes
	// (including edit-batch commit) make the same locality decision.
	private readonly Dictionary<ulong,SmoothTerrainEditCommand> _editRevisionCommands = new();
	private SmoothTerrainEditSnapshot? _editSnapshot;
	private RingLodSurfaceEditSnapshot? _cachedLodSurfaceEditSnapshot;
	private SmoothTerrainVolumetricDensitySnapshot? _cachedVolumetricDensitySnapshot;
	private ulong _visualCoverageRevision = 1;
	private CancellationTokenSource _cancel = new();
	private RingTerrainSampler? _sourceSampler;
	private VoxelChunkGridManager? _sourceGrid;
	private RingTerrainSmoothBaseline? _baseline;
	private ShaderMaterial? _terrainMaterial;
	private DirectionalLight3D? _sunLight;
	private RingLodGeometrySnapshot _geometry;
	private RingCanonicalPosition _focus;
	private bool _surfaceResidencyActive = true;
	private ulong _generation;
	private ulong _editRevision;
	private const int MaxWorkerPermits = 256;
	private SemaphoreSlim _workers = new(1, MaxWorkerPermits);
	private int _appliedWorkerCount = 1;
	private int _activeWorkers;
	private int _maxActiveWorkers;
	private int _ownedPresenters;
	private TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private Node3D? _streamingFocus; private double _focusElapsed; private SmoothTerrainChunkCoordinate? _lastFocusCenter; private SmoothTerrainChunkCoordinate? _lastHotPatchAnchor; private ulong _nextCommandId=1; private long _nextSequence=1;
	private bool _fastTravelFocusSuspended;
	private double _fastTravelStoppedElapsedSeconds;
	private double _fastTravelSuspendedElapsedSeconds;
	private double _materialElapsed;
	private bool _terrainMaterialUniformsValid;
	private Vector3 _lastTerrainMaterialRingCenter;
	private Vector3 _lastTerrainMaterialCameraPosition;
	private Vector3 _lastTerrainMaterialSunDirection;
	private float _lastTerrainMaterialDayStartDot;
	private float _lastTerrainMaterialDayFullDot;
	private Vector3 _lastTerrainMaterialNightTint;
	private float _lastTerrainMaterialNightDirectLightFactor;
	private float _lastTerrainMaterialDayDirectLightFactor;
	private float _lastTerrainMaterialShadowFloor;
	private double _metricsElapsed;
	private double _hotPatchElapsed;
	private int _cancelRequestCount;
	private long _auxiliaryCancelRequestCount;
	private double _lastAggregateInstallMilliseconds, _maxAggregateInstallMilliseconds;
	private int _lastAggregateInstalls, _maxAggregateInstalls;
	private ulong _installBudgetExceededFrames;
	private long _telemetryPeakBytes;
	private int _hotPatchMergeCount;
	private int _lastRadiusAround=int.MinValue,_lastRadiusAcross=int.MinValue,_lastRadiusVertical=int.MinValue,_lastCollisionRadius=int.MinValue;
	private bool _lastFocusCenteredCollisionCoverage,_lastFocusCenteredVisualCoverage,_lastMediumVolumetricVisualCoverage,_lastCoarseVolumetricVisualCoverage,_lastBridgeShells,_lastHotChunkPatches;
	private int _lastFocusVisualRadiusAround=int.MinValue,_lastFocusVisualRadiusAcross=int.MinValue,_lastFocusVisualRadiusVertical=int.MinValue;
	private int _lastMediumVisualRadiusAround=int.MinValue,_lastMediumVisualRadiusAcross=int.MinValue,_lastMediumVisualRadiusVertical=int.MinValue;
	private int _lastCoarseVisualRadiusAround=int.MinValue,_lastCoarseVisualRadiusAcross=int.MinValue,_lastCoarseVisualRadiusVertical=int.MinValue;
	private float _lastCoarseUndergroundActivationDepth=float.NaN;
	private int _lastCoarseRetainMargin=int.MinValue;
	private float _lastCoarseActivationHysteresis=float.NaN;
	private readonly List<SmoothTerrainEditCommand> _queryScratch=new();
	// Local stamps are interactive work.  Keep their small affected set ahead
	// of ordinary streaming so an edit never waits behind a just-entered visual
	// window.
	private readonly HashSet<SmoothTerrainChunkCoordinate> _urgentRebuilds=new();
	private readonly Dictionary<SmoothTerrainChunkCoordinate,SmoothTerrainChunkSnapshot> _urgentSnapshots=new();
	private readonly Dictionary<SmoothTerrainChunkCoordinate,byte> _urgentPatchMasks=new();
	private readonly HashSet<SmoothTerrainChunkCoordinate> _batchRetained=new();
	private readonly HashSet<SmoothTerrainChunkCoordinate> _pendingEditAffected=new();
	private readonly Dictionary<SmoothTerrainChunkCoordinate,byte> _pendingEditPatchMasks=new();
	private readonly Dictionary<SmoothTerrainChunkCoordinate,SmoothTerrainChunkSnapshot> _pendingEditSnapshots=new();
	private readonly List<SmoothTerrainEditCommand> _pendingEditCommands=new();
	private bool _pendingEditSnapshotsValid=true;
	private bool _editRefreshQueued;
	private EditBatch? _editBatch;
	private bool _refreshAfterBatchCommit;
	private readonly object _testGateLock=new();
	private TaskCompletionSource? _testWorkerGate;
	private CaveLodProfile? _caveProfileOverride;
	public CaveLodProfile? CaveProfileOverride=>_caveProfileOverride;
	/// <summary>Stores a sanitized transient profile; the per-frame change detection applies it. Null restores the exported settings.</summary>
	public void SetCaveProfileOverride(CaveLodProfile? profile)
	{
		if(profile is not { } p){_caveProfileOverride=null;return;}
		_caveProfileOverride=new CaveLodProfile(Math.Clamp(p.CoarseRadiusAround,0,4),Math.Clamp(p.CoarseRadiusAcross,0,4),Math.Clamp(p.CoarseRadiusVertical,0,4),
			Math.Clamp(p.WorkerBonus,0,64),float.IsFinite(p.InstallBudgetMilliseconds)?Math.Clamp(p.InstallBudgetMilliseconds,0.25f,16f):3f,Math.Clamp(p.MaxInstallsPerFrame,1,64));
	}
	private int EffectiveCoarseVisualRadiusAround=>_caveProfileOverride?.CoarseRadiusAround??CoarseVisualRadiusAround;
	private int EffectiveCoarseVisualRadiusAcross=>_caveProfileOverride?.CoarseRadiusAcross??CoarseVisualRadiusAcross;
	private int EffectiveCoarseVisualRadiusVertical=>_caveProfileOverride?.CoarseRadiusVertical??CoarseVisualRadiusVertical;
	/// <summary>The profile only raises the user's worker count (bounded by the machine), never lowers it.</summary>
	public int EffectiveMaxWorkers
	{
		get
		{
			int bonus=_caveProfileOverride?.WorkerBonus??0;
			int workers=bonus<=0?MaxWorkers:Math.Max(MaxWorkers,Math.Min(MaxWorkers+bonus,Math.Max(1,System.Environment.ProcessorCount-2)));
			return Math.Clamp(workers,1,MaxWorkerPermits);
		}
	}
	public int EffectiveMaxInstallsPerFrame=>_caveProfileOverride is { } p?Math.Max(MaxInstallsPerFrame,p.MaxInstallsPerFrame):MaxInstallsPerFrame;
	public float EffectiveInstallBudgetMilliseconds=>_caveProfileOverride is { } p?Math.Max(InstallBudgetMilliseconds,p.InstallBudgetMilliseconds):InstallBudgetMilliseconds;
	public CaveLodProfileStatus CaptureCaveLodProfileStatus()
	{
		CaveLodTelemetry t=CaptureCaveLodTelemetry();
		return new(IsCoarseUndergroundFocus(),t.Coarse.Desired,t.Coarse.Resident,t.Coarse.Ready,t.Coarse.Visible,t.TotalEstimatedBytes,EffectiveMaxWorkers,EffectiveCoarseVisualRadiusAround,EffectiveCoarseVisualRadiusAcross,EffectiveCoarseVisualRadiusVertical,CountCoarseDesiredReady());
	}
	private int CountCoarseDesiredReady(){int ready=0;foreach(SmoothTerrainChunkCoordinate c in _coarseVolumetricVisualDesired)if(_coarseEntries.TryGetValue(c,out CoarseEntry? e)&&e.Ready)ready++;return ready;}
	/// <summary>Test-only: coarse sectors scheduled since startup.</summary>
	public int CoarseScheduledCountForTests{get;private set;}
	/// <summary>Test-only: coarse underground activation latch flips since startup.</summary>
	public int CoarseUndergroundFocusTransitionCountForTests{get;private set;}
	/// <summary>Test-only: whole-tier cave LOD presentation clears that removed at least one entry.</summary>
	public int CaveLodPresentationClearCountForTests{get;private set;}
	public ulong? CaptureCoarseEntryGenerationForTests(SmoothTerrainChunkCoordinate coordinate)=>_coarseEntries.TryGetValue(coordinate,out CoarseEntry? entry)?entry.Generation:null;
	/// <summary>Test-only: leaves completed worker output queued until explicitly cleared.</summary>
	public bool SuspendInstallsForTests { get; set; }
	/// <summary>Test-only: pauses a worker before snapshot capture; inert unless explicitly called.</summary>
	public void PauseWorkersForTests(){lock(_testGateLock)_testWorkerGate=new(TaskCreationOptions.RunContinuationsAsynchronously);}
	public void ReleaseWorkersForTests(){lock(_testGateLock){_testWorkerGate?.TrySetResult();_testWorkerGate=null;}}
	/// <summary>Test-only rebuild using the same desired-coordinate scheduler and current revision.</summary>
	public bool RebuildCoordinateForTests(SmoothTerrainChunkCoordinate coordinate)
	{
		if(_baseline==null||!_entries.TryGetValue(coordinate,out Entry? entry))return false;
		SmoothTerrainVolumetricDensitySnapshot? field=CaptureVolumetricDensitySnapshot();
		if(field==null)return false;
		RemoveEntry(coordinate,entry); _generation++; Schedule(coordinate,_generation,new SmoothTerrainEditSnapshot(_commands,_editRevision,_baseline.AroundLength),field); UpdateCurrentMetrics(); return true;
	}
	/// <summary>Test-only immutable current presenter origins.</summary>
	public IReadOnlyDictionary<SmoothTerrainChunkCoordinate,Vector3> CapturePresenterPositionsForTests(){var r=new Dictionary<SmoothTerrainChunkCoordinate,Vector3>();foreach(var p in _entries)if(p.Value.Presenter!=null)r[p.Key]=p.Value.Presenter.Position;return r;}
	public SmoothTerrainRuntimeMetrics Metrics { get; private set; }
	public void ResetMetricPeaks() { Interlocked.Exchange(ref _maxActiveWorkers,0); Metrics = Metrics with { MaxActiveWorkers=0,MaxWorkerMilliseconds = 0, MaxInstallMilliseconds = 0, LastWorkerMilliseconds = 0, LastInstallMilliseconds = 0 }; }
	public int ReadyRenderCount { get; private set; }
	public int ReadyCollisionCount { get; private set; }
	public int CreatedPresenterCount { get; private set; }
	public int ReusedPresenterCount { get; private set; }
	public int PoolCount => _pool.Count;
	public int TaskCount { get { lock(_lifetimeGate) return _trackedTasks.Count; } }
	/// <summary>Mesh jobs waiting for bounded submission, by tier order base/near-transition/medium/bridge/coarse/critical-base.</summary>
	public IReadOnlyList<int> DeferredWorkCounts
	{
		get { lock (_lifetimeGate) return _deferredWork.Select(queue => queue.Count).ToArray(); }
	}
	public int DeferredWorkCount { get { lock (_lifetimeGate) return _deferredWork.Sum(queue => queue.Count); } }
	public ulong DeferredWorkSubmittedCount => _deferredSubmitted;
	public ulong DeferredWorkCancelledCount => _deferredCancelled;
	public int ReadyResultCount => _ready.Count;
	public int PresenterCount => _ownedPresenters;
	/// <summary>True while noclip fast travel intentionally retains the last installed terrain window.</summary>
	public bool FastTravelFocusSuspended => _fastTravelFocusSuspended;
	/// <summary>Number of focus/configuration refreshes deliberately deferred during fast travel.</summary>
	public ulong FastTravelDeferredRefreshCount { get; private set; }
	/// <summary>Number of forced current-focus refreshes issued after fast travel resumes.</summary>
	public ulong FastTravelResumeRefreshCount { get; private set; }
	/// <summary>Cumulative seconds spent retaining installed terrain during fast travel.</summary>
	public double FastTravelSuspendedSeconds => _fastTravelSuspendedElapsedSeconds;
	/// <summary>Allocation-free aggregate resource view for production health monitoring.</summary>
	public readonly record struct ResourceDiagnostics(
		int PooledPresenterCount,
		int AuxiliaryResidentEntryCount,
		int AuxiliaryReadyResultCount,
		int TrackedTaskCount,
		int TrackedPresenterCount,
		long EstimatedResidentBytes,
		int BaseActivePresenterCount,
		int BasePooledPresenterCount,
		int NearTransitionActivePresenterCount,
		int NearTransitionPooledPresenterCount,
		int MediumActivePresenterCount,
		int MediumPooledPresenterCount,
		int Bridge4ActivePresenterCount,
		int Bridge4PooledPresenterCount,
		int Bridge8ActivePresenterCount,
		int Bridge8PooledPresenterCount,
		int Bridge16ActivePresenterCount,
		int Bridge16PooledPresenterCount,
		int Bridge32ActivePresenterCount,
		int Bridge32PooledPresenterCount,
		int CoarseActivePresenterCount,
		int CoarsePooledPresenterCount);
	/// <summary>Immutable, allocation-conscious cave LOD health snapshot. Counts are sampled on
	/// the main thread; ConcurrentQueue enumeration is intentionally approximate.</summary>
	public readonly record struct CaveLodTierTelemetry(int Desired, int Resident, int Ready, int Visible, int QueuedReady, int PooledPresenters, long RetainedBytes);
	public readonly record struct CaveLodTelemetry(
		CaveLodTierTelemetry Base, CaveLodTierTelemetry NearTransition, CaveLodTierTelemetry Medium,
		CaveLodTierTelemetry Bridge4, CaveLodTierTelemetry Bridge8, CaveLodTierTelemetry Bridge16, CaveLodTierTelemetry Bridge32, CaveLodTierTelemetry Coarse,
		int DeferredQueueDepth, int ActiveWorkers, int TrackedWork, ulong DeferredSubmitted, ulong DeferredCancelled, long CancellationRequests,
		double LastInstallMilliseconds, double MaxInstallMilliseconds, int LastInstalls, int MaxInstalls, ulong BudgetExceededFrames, int PendingReadyResults,
		long QueuedReadyBytes, long ResidentAndPoolBytes, long DensityCacheBytes,
		long TotalEstimatedBytes, long PeakEstimatedBytes);

	public CaveLodTelemetry CaptureCaveLodTelemetry()
	{
		long queued=0, retained=0;
		CaveLodTierTelemetry baseTier=CaptureBaseTier(ref queued,ref retained);
		CaveLodTierTelemetry near=CaptureNearTier(ref queued,ref retained);
		CaveLodTierTelemetry medium=CaptureMediumTier(ref queued,ref retained);
		CaveLodTierTelemetry coarse=CaptureCoarseTier(ref queued,ref retained);
		CaveLodTierTelemetry b4=default,b8=default,b16=default,b32=default;
		foreach(BridgeTier tier in _bridgeTiers){CaveLodTierTelemetry t=CaptureBridgeTier(tier,ref queued,ref retained);switch(tier.Spacing){case 4:b4=t;break;case 8:b8=t;break;case 16:b16=t;break;case 32:b32=t;break;}}
		long densityCache=0;
		if(_cachedVolumetricDensitySnapshot is { } densityField)
		{
			densityCache=AddSaturating(
				densityField.CaptureTransitionLatticeCacheDiagnostics().RetainedBytes,
				densityField.CaptureRegularChunkCacheDiagnostics().RetainedBytes);
		}
		long total=AddSaturating(AddSaturating(retained,queued),densityCache); long peak=Interlocked.Read(ref _telemetryPeakBytes);
		while(total>peak){long prior=Interlocked.CompareExchange(ref _telemetryPeakBytes,total,peak);if(prior==peak){peak=total;break;}peak=prior;}
		int tracked; int deferred=0; ulong submitted,cancelled; lock(_lifetimeGate){tracked=_trackedTasks.Count;foreach(Queue<DeferredMeshWork> queue in _deferredWork)deferred+=queue.Count;submitted=_deferredSubmitted;cancelled=_deferredCancelled;}
		int pending=SafeCount(_ready)+SafeCount(_nearTransitionReady)+SafeCount(_mediumReady)+SafeCount(_coarseReady);foreach(BridgeTier tier in _bridgeTiers)pending+=SafeCount(tier.Ready);
		int activePermits=Math.Clamp(Math.Max(1,_appliedWorkerCount)-Math.Max(0,_workers.CurrentCount),0,Math.Max(1,_appliedWorkerCount));
		return new(baseTier,near,medium,b4,b8,b16,b32,coarse,deferred,activePermits,tracked,submitted,cancelled,Math.Max(0,Volatile.Read(ref _cancelRequestCount)+Interlocked.Read(ref _auxiliaryCancelRequestCount)),_lastAggregateInstallMilliseconds,_maxAggregateInstallMilliseconds,_lastAggregateInstalls,_maxAggregateInstalls,_installBudgetExceededFrames,pending,queued,retained,densityCache,total,Math.Max(total,Interlocked.Read(ref _telemetryPeakBytes)));
	}
	private CaveLodTierTelemetry CaptureBaseTier(ref long queued,ref long retained){int ready=0,visible=0;foreach(Entry e in _entries.Values){if(e.Ready)ready++;if(e.Presenter?.Visible==true)visible++;if(e.Patches!=null)foreach(SmoothTerrainPresenter? p in e.Patches)if(p?.Visible==true)visible++;AddEntryBytes(e,ref retained);if(e.TargetSnapshot!=null)retained=AddSaturating(retained,SnapshotBytes(e.TargetSnapshot));}foreach(SmoothTerrainPresenter p in _pool)retained=AddSaturating(retained,p.RetainedByteSize);foreach(SmoothTerrainPresenter p in _physicsDeferredReleases)retained=AddSaturating(retained,p.RetainedByteSize);foreach(Result r in _ready)queued=AddSaturating(queued,ResultQueuedBytes(r));return new(_residentBuildDesired.Count,_entries.Count,ready,visible,SafeCount(_ready),_pool.Count,retained);}
	private CaveLodTierTelemetry CaptureNearTier(ref long queued,ref long retained){int ready=0,visible=0;long bytes=0;foreach(NearTransitionEntry e in _nearTransitionEntries.Values){if(e.Ready)ready++;if(e.Presenter.Visible)visible++;bytes=AddSaturating(bytes,e.Presenter.RetainedByteSize);}foreach(SmoothTerrainPresenter p in _nearTransitionPool)bytes=AddSaturating(bytes,p.RetainedByteSize);foreach(NearTransitionResult r in _nearTransitionReady)queued=AddSaturating(queued,MeshBytes(r.Mesh));retained=AddSaturating(retained,bytes);return new(_nearTransitionDesired.Count,_nearTransitionEntries.Count,ready,visible,SafeCount(_nearTransitionReady),_nearTransitionPool.Count,bytes);}
	private CaveLodTierTelemetry CaptureMediumTier(ref long queued,ref long retained){int ready=0,visible=0;long bytes=0;foreach(MediumEntry e in _mediumEntries.Values){if(e.Ready)ready++;visible+=e.Presenters.Count(p=>p.Visible);foreach(SmoothTerrainPresenter p in e.Presenters)bytes=AddSaturating(bytes,p.RetainedByteSize);}foreach(SmoothTerrainPresenter p in _mediumPool)bytes=AddSaturating(bytes,p.RetainedByteSize);foreach(MediumResult r in _mediumReady)foreach(SmoothTerrainMeshData mesh in r.Meshes)queued=AddSaturating(queued,MeshBytes(mesh));retained=AddSaturating(retained,bytes);return new(_mediumVolumetricVisualDesired.Count,_mediumEntries.Count,ready,visible,SafeCount(_mediumReady),_mediumPool.Count,bytes);}
	private CaveLodTierTelemetry CaptureCoarseTier(ref long queued,ref long retained){int ready=0,visible=0;long bytes=0;foreach(CoarseEntry e in _coarseEntries.Values){if(e.Ready)ready++;if(e.Presenter.Visible)visible++;bytes=AddSaturating(bytes,e.Presenter.RetainedByteSize);}foreach(SmoothTerrainPresenter p in _coarsePool)bytes=AddSaturating(bytes,p.RetainedByteSize);foreach(CoarseResult r in _coarseReady)queued=AddSaturating(queued,MeshBytes(r.Mesh));retained=AddSaturating(retained,bytes);return new(_coarseVolumetricVisualDesired.Count,_coarseEntries.Count,ready,visible,SafeCount(_coarseReady),_coarsePool.Count,bytes);}
	private CaveLodTierTelemetry CaptureBridgeTier(BridgeTier t,ref long queued,ref long retained){int ready=0,visible=0;long bytes=0;foreach(BridgeEntry e in t.Entries.Values){if(e.Ready)ready++;if(e.Presenter.Visible)visible++;bytes=AddSaturating(bytes,e.Presenter.RetainedByteSize);}foreach(SmoothTerrainPresenter p in t.Pool)bytes=AddSaturating(bytes,p.RetainedByteSize);foreach(BridgeResult r in t.Ready)queued=AddSaturating(queued,MeshBytes(r.Mesh));retained=AddSaturating(retained,bytes);return new(t.Desired.Count,t.Entries.Count,ready,visible,SafeCount(t.Ready),t.Pool.Count,bytes);}
	private static int SafeCount<T>(ConcurrentQueue<T> queue)=>Math.Max(0,queue.Count);
	private static long AddSaturating(long a,long b)=>b<=0?a:a>long.MaxValue-b?long.MaxValue:a+b;
	private static long MeshBytes(SmoothTerrainMeshData m)=>AddSaturating(AddSaturating((long)m.Vertices.Length*12,(long)m.Normals.Length*12),AddSaturating(AddSaturating((long)m.Materials.Length*4,(long)m.Indices.Length*4),(long)m.CollisionFaces.Length*12));
	private long ResultQueuedBytes(Result r){long bytes=MeshBytes(r.Mesh);if(!_entries.TryGetValue(r.Coordinate,out Entry? entry)||!ReferenceEquals(entry.TargetSnapshot,r.Snapshot))bytes=AddSaturating(bytes,SnapshotBytes(r.Snapshot));if(r.Patches!=null)foreach(SmoothTerrainMeshData? patch in r.Patches)if(patch!=null)bytes=AddSaturating(bytes,MeshBytes(patch));return bytes;}
	private static long SnapshotBytes(SmoothTerrainChunkSnapshot snapshot)=>AddSaturating((long)snapshot.Densities.Length*4,(long)snapshot.Materials.Length*4);
	private static void AddEntryBytes(Entry e,ref long bytes){if(e.Presenter!=null)bytes=AddSaturating(bytes,e.Presenter.RetainedByteSize);if(e.Patches!=null)foreach(SmoothTerrainPresenter? p in e.Patches)if(p!=null)bytes=AddSaturating(bytes,p.RetainedByteSize);}
	/// <summary>
	/// Captures all smooth-terrain presentation tiers without LINQ or temporary collections.
	/// Pooled presenters normally retain no mesh bytes after Reset, but are included in the
	/// byte total so the value remains correct if that implementation changes.
	/// </summary>
	public ResourceDiagnostics CaptureResourceDiagnostics()
	{
		int pooled = _pool.Count + _nearTransitionPool.Count + _mediumPool.Count + _coarsePool.Count;
		int auxiliaryEntries = _nearTransitionEntries.Count + _mediumEntries.Count + _coarseEntries.Count;
		int auxiliaryReady = _nearTransitionReady.Count + _mediumReady.Count + _coarseReady.Count;
		int presenters = 0;
		int baseActive = 0;
		long bytes = 0;
		foreach (SmoothTerrainPresenter presenter in _pool) { presenters++; bytes += presenter.RetainedByteSize; }
		foreach (SmoothTerrainPresenter presenter in _physicsDeferredReleases) { presenters++; bytes += presenter.RetainedByteSize; }
		foreach (Entry entry in _entries.Values)
		{
			if (entry.Presenter != null) { presenters++; baseActive++; bytes += entry.Presenter.RetainedByteSize; }
			if (entry.Patches != null) foreach (SmoothTerrainPresenter? patch in entry.Patches) if (patch != null) { presenters++; baseActive++; bytes += patch.RetainedByteSize; }
		}
		foreach (SmoothTerrainPresenter presenter in _nearTransitionPool) { presenters++; bytes += presenter.RetainedByteSize; }
		foreach (NearTransitionEntry entry in _nearTransitionEntries.Values) { presenters++; bytes += entry.Presenter.RetainedByteSize; }
		foreach (SmoothTerrainPresenter presenter in _mediumPool) { presenters++; bytes += presenter.RetainedByteSize; }
		foreach (MediumEntry entry in _mediumEntries.Values) foreach(SmoothTerrainPresenter presenter in entry.Presenters) { presenters++; bytes += presenter.RetainedByteSize; }
		foreach (SmoothTerrainPresenter presenter in _coarsePool) { presenters++; bytes += presenter.RetainedByteSize; }
		foreach (CoarseEntry entry in _coarseEntries.Values) { presenters++; bytes += entry.Presenter.RetainedByteSize; }
		int bridge4Active = 0, bridge8Active = 0, bridge16Active = 0, bridge32Active = 0;
		int bridge4Pooled = 0, bridge8Pooled = 0, bridge16Pooled = 0, bridge32Pooled = 0;
		foreach (BridgeTier tier in _bridgeTiers)
		{
			pooled += tier.Pool.Count;
			auxiliaryEntries += tier.Entries.Count;
			auxiliaryReady += tier.Ready.Count;
			switch (tier.Spacing)
			{
				case 4: bridge4Active = tier.Entries.Count; bridge4Pooled = tier.Pool.Count; break;
				case 8: bridge8Active = tier.Entries.Count; bridge8Pooled = tier.Pool.Count; break;
				case 16: bridge16Active = tier.Entries.Count; bridge16Pooled = tier.Pool.Count; break;
				case 32: bridge32Active = tier.Entries.Count; bridge32Pooled = tier.Pool.Count; break;
			}
			foreach (SmoothTerrainPresenter presenter in tier.Pool) { presenters++; bytes += presenter.RetainedByteSize; }
			foreach (BridgeEntry entry in tier.Entries.Values) { presenters++; bytes += entry.Presenter.RetainedByteSize; }
		}
		int tasks;
		lock (_lifetimeGate) tasks = _trackedTasks.Count;
		return new ResourceDiagnostics(
			pooled, auxiliaryEntries, auxiliaryReady, tasks, presenters, Math.Max(0, bytes),
			baseActive, _pool.Count,
			_nearTransitionEntries.Count, _nearTransitionPool.Count,
			_mediumEntries.Count, _mediumPool.Count,
			bridge4Active, bridge4Pooled, bridge8Active, bridge8Pooled,
			bridge16Active, bridge16Pooled, bridge32Active, bridge32Pooled,
			_coarseEntries.Count, _coarsePool.Count);
	}
	/// <summary>Whether local smooth terrain is intentionally resident at the current focus.</summary>
	public bool SurfaceResidencyActive => _surfaceResidencyActive;
	/// <summary>Test/diagnostic count of resident parents using the 16^3 representation.</summary>
	public int HotParentCount => _entries.Values.Count(entry=>entry.HotPatches);
	/// <summary>Test/diagnostic count of installed 16^3 render/collision presenters.</summary>
	public int HotPatchPresenterCount => _entries.Values.Sum(entry=>entry.Patches?.Count(patch=>patch!=null)??0);
	public int HotPatchMergeCount => Volatile.Read(ref _hotPatchMergeCount);
	/// <summary>Test-only retained edit history required by stale coarse LOD entries.</summary>
	public int RetainedEditRevisionHistoryCountForTests => _editRevisionCommands.Count;
	/// <summary>Production near-cave tier: the existing 1 m, 32-cell regular
	/// mesher, rendered independently around the 3D focus and collision-ready
	/// only when the separate physics owner requests it.</summary>
	public readonly record struct NearVolumetricMeshContract(int SampleSpacingMeters,int CellsPerParent,bool CollisionCapable)
	{ public bool IsValid => SampleSpacingMeters==1 && CellsPerParent==SmoothTerrainChunkSnapshot.Cells && CollisionCapable; }
	public NearVolumetricMeshContract CaptureNearVolumetricMeshContract() => new(1,SmoothTerrainChunkSnapshot.Cells,true);
	/// <summary>Medium cave tier: 32 regular cells sampled every 2 m across a
	/// 64 m sector. It consumes the authoritative lattice and is render-only.
	/// The two-cell lattice halo makes sector edge gradients deterministic.</summary>
	public readonly record struct MediumVolumetricMeshContract(int SampleSpacingMeters,int CellsPerSector,int SectorExtentMeters,bool CollisionCapable)
	{ public bool IsValid => SampleSpacingMeters==2 && CellsPerSector==32 && SectorExtentMeters==64 && !CollisionCapable; }
	public MediumVolumetricMeshContract CaptureMediumVolumetricMeshContract()=>new(2,32,64,false);
	/// <summary>Megacavern tier: 32 cells at 64 m spacing, a 2048 m sector,
	/// with a two-sample gradient halo and no collision representation.</summary>
	public readonly record struct CoarseVolumetricMeshContract(int SampleSpacingMeters,int CellsPerSector,int SectorExtentMeters,bool CollisionCapable)
	{ public bool IsValid=>SampleSpacingMeters==64&&CellsPerSector==32&&SectorExtentMeters==2048&&!CollisionCapable; }
	public CoarseVolumetricMeshContract CaptureCoarseVolumetricMeshContract()=>new(64,32,2048,false);
	public readonly record struct BridgeVolumetricMeshContract(int SampleSpacingMeters,int CellsPerSector,int SectorExtentMeters,bool CollisionCapable)
	{ public bool IsValid=> (SampleSpacingMeters==4||SampleSpacingMeters==8||SampleSpacingMeters==16||SampleSpacingMeters==32)&&CellsPerSector==32&&SectorExtentMeters==SampleSpacingMeters*32&&!CollisionCapable; }
	public IReadOnlyList<BridgeVolumetricMeshContract> CaptureBridgeVolumetricMeshContracts()=>_bridgeTiers.Select(t=>new BridgeVolumetricMeshContract(t.Spacing,32,t.Extent,false)).ToArray();
	/// <summary>Independent ownership configuration for visual streaming, near physics, and 16 m edit subdivision.</summary>
	public readonly record struct CoverageOwnershipContract(
		int VisualRadiusAround,
		int VisualRadiusAcross,
		int VisualRadiusVertical,
		int CollisionRadius,
		bool FocusCenteredCollisionEnabled,
		bool FocusCenteredVisualEnabled,
		int FocusVisualRadiusAround,
		int FocusVisualRadiusAcross,
		int FocusVisualRadiusVertical,
		bool HotEditSubdivisionEnabled,
		int HotEditParentExtentPerAxis)
	{
		public bool IsValid => VisualRadiusAround >= 0 && VisualRadiusAcross >= 0
			&& VisualRadiusVertical >= 0 && CollisionRadius >= 0
			&& FocusVisualRadiusAround >= 0 && FocusVisualRadiusAcross >= 0 && FocusVisualRadiusVertical >= 0
			&& HotEditParentExtentPerAxis == 2;
	}
	/// <summary>Current independent coverage-set sizes. ResidentBuildDesired is their scheduling union, including temporary atomic-edit retention.</summary>
	public readonly record struct CoverageOwnershipSnapshot(
		int SurfaceVisualDesired,
		int NearVolumetricVisualDesired,
		int MediumVolumetricVisualDesired,
		int CoarseVolumetricVisualDesired,
		int VisualDesired,
		int CollisionDesired,
		int EditSubdivisionDesired,
		int ResidentBuildDesired);
	public CoverageOwnershipContract CaptureCoverageOwnershipContract() => new(
		Math.Max(0, RadiusAround), Math.Max(0, RadiusAcross), Math.Max(0, RadiusVertical),
		Math.Max(0, CollisionRadius), EnableFocusCenteredCollisionCoverage,
		EnableFocusCenteredVisualCoverage, Math.Max(0, FocusVisualRadiusAround), Math.Max(0, FocusVisualRadiusAcross), Math.Max(0, FocusVisualRadiusVertical),
		EnableHotChunkPatches, 2);
	public CoverageOwnershipSnapshot CaptureCoverageOwnershipSnapshot() => new(
		_surfaceVisualDesired.Count, _nearVolumetricVisualDesired.Count, _mediumVolumetricVisualDesired.Count, _coarseVolumetricVisualDesired.Count, _visualDesired.Count,
		_collisionDesired.Count, _editSubdivisionDesired.Count, _residentBuildDesired.Count);
	/// <summary>Test/telemetry ownership predicates; readiness remains representation-specific.</summary>
	public bool IsVisualCoverageOwnerForTests(SmoothTerrainChunkCoordinate coordinate) => _visualDesired.Contains(coordinate);
	public bool IsSurfaceVisualCoverageOwnerForTests(SmoothTerrainChunkCoordinate coordinate) => _surfaceVisualDesired.Contains(coordinate);
	public bool IsNearVolumetricVisualCoverageOwnerForTests(SmoothTerrainChunkCoordinate coordinate) => _nearVolumetricVisualDesired.Contains(coordinate);
	public int NearTransitionEntryCountForTests => _nearTransitionEntries.Count;
	public int NearTransitionTriangleCountForTests => _nearTransitionEntries.Values.Sum(entry=>entry.Presenter.RenderTriangleCount);
	public bool IsNearTransitionVisibleForTests(SmoothTerrainChunkCoordinate coordinate) => _nearTransitionEntries.TryGetValue(coordinate,out NearTransitionEntry? entry)&&entry.Presenter.Visible&&entry.Presenter.HasRenderMesh;
	public bool HasNearTransitionCollisionForTests(SmoothTerrainChunkCoordinate coordinate) => _nearTransitionEntries.TryGetValue(coordinate,out NearTransitionEntry? entry)&&entry.Presenter.HasCollision;
	public byte CaptureNearTransitionMaskForTests(SmoothTerrainChunkCoordinate coordinate) => _nearTransitionEntries.TryGetValue(coordinate,out NearTransitionEntry? entry)?entry.FaceMask:(byte)0;
	public bool IsMediumVolumetricVisualCoverageOwnerForTests(SmoothTerrainChunkCoordinate coordinate) => _mediumVolumetricVisualDesired.Contains(coordinate);
	public bool IsMediumVolumetricVisibleForTests(SmoothTerrainChunkCoordinate coordinate) => _mediumEntries.TryGetValue(coordinate,out MediumEntry? entry)&&entry.Presenters.Any(p=>p.Visible&&p.HasRenderMesh);
	public bool IsMediumChildVisibleForTests(SmoothTerrainChunkCoordinate sector,SmoothTerrainChunkCoordinate child) => _mediumEntries.TryGetValue(sector,out MediumEntry? entry)&&TryGetMediumChildIndex(sector,child,out int index)&&entry.Presenters[index].Visible&&entry.Presenters[index].HasRenderMesh;
	public bool IsFineChildReadyVisualOwnerForTests(SmoothTerrainChunkCoordinate child) => IsFineChildReadyVisualOwner(child);
	public bool HasMediumVolumetricCollisionForTests(SmoothTerrainChunkCoordinate coordinate) => _mediumEntries.TryGetValue(coordinate,out MediumEntry? entry)&&entry.Presenters.Any(p=>p.HasCollision);
	public SmoothTerrainVolumetricDensityIdentity? CaptureMediumVolumetricIdentityForTests(SmoothTerrainChunkCoordinate coordinate) => _mediumEntries.TryGetValue(coordinate,out MediumEntry? entry)?entry.Identity:null;
	/// <summary>Medium-only proof that a sector was deliberately rejected as
	/// uniform; this is never a collision claim.</summary>
	public int MediumUniformRejectedCountForTests => _mediumEntries.Values.Count(entry=>entry.UniformRejected);
	public SmoothTerrainDensityClassification? CaptureMediumVolumetricClassificationForTests(SmoothTerrainChunkCoordinate coordinate)
		=> _mediumEntries.TryGetValue(coordinate,out MediumEntry? entry)?entry.Classification:null;
	public int BridgeVolumetricEntryCountForTests=>_bridgeTiers.Sum(t=>t.Entries.Count);
	public bool HasBridgeVolumetricCollisionForTests=>_bridgeTiers.Any(t=>t.Entries.Values.Any(e=>e.Presenter.HasCollision));
	public int BridgeUniformRejectedCountForTests=>_bridgeTiers.Sum(t=>t.Entries.Values.Count(e=>e.UniformRejected));
	public IReadOnlyDictionary<int,int> CaptureBridgeEntryCountsForTests()=>_bridgeTiers.ToDictionary(t=>t.Spacing,t=>t.Entries.Count);
	public IReadOnlyDictionary<int,int> CaptureBridgeTransitionTriangleCountsForTests()=>_bridgeTiers.ToDictionary(t=>t.Spacing,t=>t.Entries.Values.Sum(e=>e.TransitionTriangleCount));
	public byte CaptureBridgeTransitionMaskForTests(int spacing,SmoothTerrainChunkCoordinate coordinate)=>_bridgeTiers.FirstOrDefault(t=>t.Spacing==spacing)?.Entries.TryGetValue(coordinate,out BridgeEntry? e)==true?e.TransitionMask:(byte)0;
	public bool IsCoarseVolumetricVisualCoverageOwnerForTests(SmoothTerrainChunkCoordinate coordinate)=>_coarseVolumetricVisualDesired.Contains(coordinate);
	public bool IsCoarseVolumetricVisibleForTests(SmoothTerrainChunkCoordinate coordinate)=>_coarseEntries.TryGetValue(coordinate,out CoarseEntry? entry)&&entry.Presenter.Visible&&entry.Presenter.HasRenderMesh;
	public bool HasCoarseVolumetricCollisionForTests(SmoothTerrainChunkCoordinate coordinate)=>_coarseEntries.TryGetValue(coordinate,out CoarseEntry? entry)&&entry.Presenter.HasCollision;
	public SmoothTerrainVolumetricDensityIdentity? CaptureCoarseVolumetricIdentityForTests(SmoothTerrainChunkCoordinate coordinate)=>_coarseEntries.TryGetValue(coordinate,out CoarseEntry? entry)?entry.Identity:null;
	public int CoarseUniformRejectedCountForTests=>_coarseEntries.Values.Count(entry=>entry.UniformRejected);
	public SmoothTerrainDensityClassification? CaptureCoarseVolumetricClassificationForTests(SmoothTerrainChunkCoordinate coordinate)=>_coarseEntries.TryGetValue(coordinate,out CoarseEntry? entry)?entry.Classification:null;
	public IReadOnlyCollection<SmoothTerrainChunkCoordinate> CaptureCoarseVolumetricCoordinatesForTests()=>_coarseEntries.Keys.ToArray();
	public int CaveLodVisibleOwnershipOverlapCountForTests
	{
		get
		{
			int overlaps=_mediumEntries.Sum(pair=>CountVisibleMediumFineOverlaps(pair.Key,pair.Value));
			for(int i=0;i<_bridgeTiers.Length;i++)
				overlaps+=_bridgeTiers[i].Entries.Count(pair=>pair.Value.Presenter.Visible&&IsBridgeCoveredByFiner(i,pair.Key));
			overlaps+=_coarseEntries.Count(pair=>pair.Value.Presenter.Visible&&IsCoarseCoveredByBridge(pair.Key));
			return overlaps;
		}
	}
	public bool IsCollisionCoverageOwnerForTests(SmoothTerrainChunkCoordinate coordinate) => _collisionDesired.Contains(coordinate);
	public bool IsEditSubdivisionOwnerForTests(SmoothTerrainChunkCoordinate coordinate) => _editSubdivisionDesired.Contains(coordinate);
	public bool IsVisualRepresentationVisibleForTests(SmoothTerrainChunkCoordinate coordinate)
		=> _entries.TryGetValue(coordinate,out Entry? entry)
			&&(entry.Presenter?.Visible==true
				||entry.Patches?.Any(patch=>patch?.Visible==true)==true);
	public SmoothTerrainChunkCoordinate? HotPatchAnchorForTests => _lastHotPatchAnchor;
	public IReadOnlyDictionary<SmoothTerrainChunkCoordinate,byte> CaptureHotPatchMasksForTests()
	{
		var masks=new Dictionary<SmoothTerrainChunkCoordinate,byte>();
		foreach(var pair in _entries)if(pair.Value.HotPatches)masks[pair.Key]=pair.Value.LastPatchMask;
		return masks;
	}
	/// <summary>Test-only bit mask of hot-patch presenters with an installed physics shape.</summary>
	public IReadOnlyDictionary<SmoothTerrainChunkCoordinate,byte> CaptureHotPatchCollisionMasksForTests()
	{
		var masks=new Dictionary<SmoothTerrainChunkCoordinate,byte>();
		foreach(var pair in _entries)
		{
			if(!pair.Value.HotPatches||pair.Value.Patches==null)continue;
			byte mask=0;
			for(int patch=0;patch<pair.Value.Patches.Length;patch++)
				if(pair.Value.Patches[patch]?.HasCollision==true)mask|=(byte)(1<<patch);
			masks[pair.Key]=mask;
		}
		return masks;
	}
	/// <summary>Test-only nearest retained hot-patch vertex to a world-space probe.</summary>
	public string DescribeHotPatchGeometryForTests(SmoothTerrainChunkCoordinate coordinate,Vector3 worldProbe)
	{
		if(!_entries.TryGetValue(coordinate,out Entry? entry)||entry.Patches==null)return "no-hot-patches";
		var parts=new List<string>();
		for(int patch=0;patch<entry.Patches.Length;patch++)
			if(entry.Patches[patch] is SmoothTerrainPresenter presenter)
				parts.Add($"{patch}:{presenter.DescribeWorldProbeForTests(worldProbe)}");
		return string.Join("|",parts);
	}
	public bool TexturedTerrainMaterialReady => _terrainMaterial != null
		&& _terrainMaterial.GetShaderParameter("pilot_triplanar_enabled").AsBool();
	public ulong StreamingFocusPollCount { get; private set; }
	public bool IsQuiescent
	{
		get
		{
			if (_editRefreshQueued || !_tasks.IsEmpty || DeferredWorkCount != 0 || !_ready.IsEmpty || !_nearTransitionReady.IsEmpty || !_mediumReady.IsEmpty || !_coarseReady.IsEmpty || _bridgeTiers.Any(t=>!t.Ready.IsEmpty) || Volatile.Read(ref _activeWorkers) != 0) return false;
			foreach (Entry entry in _entries.Values) if (!entry.Ready) return false;
			foreach (NearTransitionEntry entry in _nearTransitionEntries.Values) if (!entry.Ready) return false;
			foreach (MediumEntry entry in _mediumEntries.Values) if (!entry.Ready) return false;
			foreach(var tier in _bridgeTiers)foreach(var entry in tier.Entries.Values)if(!entry.Ready)return false;
			foreach (CoarseEntry entry in _coarseEntries.Values) if (!entry.Ready) return false;
			return true;
		}
	}
	public readonly record struct ChunkDebugState(ulong Generation,bool Ready,bool Collision,int Builds,int Installs);
	public IReadOnlyDictionary<SmoothTerrainChunkCoordinate,ChunkDebugState> CaptureDebugState(){var r=new Dictionary<SmoothTerrainChunkCoordinate,ChunkDebugState>();foreach(var p in _entries)r[p.Key]=new(p.Value.Generation,p.Value.Ready,p.Value.Collision,p.Value.Builds,p.Value.Installs);return r;}
	public readonly record struct ChunkDiagnostics(int ScheduleCount,int CompletedBuildCount,int InstallCount,int CancelRequestCount,int StaleResultCount,bool Resident,bool Ready,bool HasRenderMesh,bool HasCollision,SmoothTerrainRevision LastInstalledRevision,ulong LastInstalledMeshHash,ulong LastInstalledGeneration);
	public IReadOnlyDictionary<SmoothTerrainChunkCoordinate,ChunkDiagnostics> CaptureDiagnostics()
	{
		var copy=new Dictionary<SmoothTerrainChunkCoordinate,ChunkDiagnostics>();
		foreach(var pair in _diagnostics) copy[pair.Key]=pair.Value.Snapshot();
		return copy;
	}
	public Task AwaitShutdownForTestsAsync() => _shutdown.Task;

	public override void _Ready()
	{
		var source = GetNodeOrNull<VoxelChunkGridManager>(SourceGridPath);
		if (source == null) { GD.PushWarning("SmoothTerrainGrid is opt-in and needs SourceGridPath."); return; }
		_sourceGrid=source; _workers.Dispose(); _appliedWorkerCount=EffectiveMaxWorkers; _workers=new SemaphoreSlim(_appliedWorkerCount,MaxWorkerPermits);
		if(!source.ValidateDeepVerticalDomains(out string domainError)){GD.PushError($"SmoothTerrainGrid deep vertical-domain configuration is invalid: {domainError}");return;}
		_sourceSampler = source.CreateIndependentTerrainSampler(); if(_sourceSampler==null){GD.PushWarning("SmoothTerrainGrid source could not create terrain sampler.");return;} _baseline = new RingTerrainSmoothBaseline(_sourceSampler,source.GetStructuralBottomGlobalY(),source.TerrainBottomGlobalY,source.ProtectedRoofThicknessMeters,source.GetLowerHullTopGlobalY(),source.GetStructuralWallTopGlobalY(),source.TerrainSettings?.EnableDesignedCaveDensityComposition==true);
		_editSnapshot=new SmoothTerrainEditSnapshot(_commands,_editRevision,_baseline.AroundLength);
		_geometry = source.CaptureLodGeometrySnapshot(); _focus = new RingCanonicalPosition(0, 0, 0);
		_streamingFocus=GetNodeOrNull<Node3D>(StreamingFocusPath);
		_sunLight=source.GetNodeOrNull<DirectionalLight3D>(source.RingSunLightPath);
		_terrainMaterial=CreateTerrainMaterial();
		source.CanonicalFloatingOriginAnchorChanged += OnGeometryChanged;
	}
	public override void _ExitTree()
	{
		Task[] pending;
		ReleaseWorkersForTests();
		ClearPendingEditRefreshState();
		lock(_lifetimeGate) { _stopping=true; _cancel.Cancel(); foreach(var queue in _deferredWork) while(queue.TryDequeue(out DeferredMeshWork? deferred)) deferred.Cancel(); foreach(var pair in _entries) if(pair.Value.RequestCancel()){Interlocked.Increment(ref _cancelRequestCount);_diagnostics.GetOrAdd(pair.Key,_=>new DiagnosticRecord()).Cancelled();} pending=_trackedTasks.ToArray(); }
		if(_sourceGrid!=null)_sourceGrid.CanonicalFloatingOriginAnchorChanged-=OnGeometryChanged;
		foreach(var entry in _entries.Values) if(entry.Presenter!=null) entry.Presenter.QueueFree();
		foreach(var entry in _entries.Values) if(entry.Patches!=null) foreach(SmoothTerrainPresenter? patch in entry.Patches) patch?.QueueFree();
		foreach(var entry in _mediumEntries.Values){if(entry.RequestCancel())Interlocked.Increment(ref _auxiliaryCancelRequestCount);foreach(SmoothTerrainPresenter presenter in entry.Presenters)presenter.QueueFree();}
		_mediumEntries.Clear();
		while(_mediumPool.Count>0)_mediumPool.Dequeue().QueueFree();
		foreach(var entry in _nearTransitionEntries.Values){if(entry.RequestCancel())Interlocked.Increment(ref _auxiliaryCancelRequestCount);entry.Presenter.QueueFree();}
		_nearTransitionEntries.Clear();
		while(_nearTransitionPool.Count>0)_nearTransitionPool.Dequeue().QueueFree();
		foreach(var entry in _coarseEntries.Values){if(entry.RequestCancel())Interlocked.Increment(ref _auxiliaryCancelRequestCount);entry.Presenter.QueueFree();}
		_coarseEntries.Clear();
		while(_coarsePool.Count>0)_coarsePool.Dequeue().QueueFree();
		foreach(var tier in _bridgeTiers){foreach(var entry in tier.Entries.Values){if(entry.RequestCancel())Interlocked.Increment(ref _auxiliaryCancelRequestCount);entry.Presenter.QueueFree();}tier.Entries.Clear();while(tier.Pool.Count>0)tier.Pool.Dequeue().QueueFree();while(tier.Ready.TryDequeue(out BridgeResult _)){}tier.Desired.Clear();}
		while(_pool.Count>0) _pool.Dequeue().QueueFree();
		while(_physicsDeferredReleases.Count>0)_physicsDeferredReleases.Dequeue().QueueFree();
		_entries.Clear();
		_visualDesired.Clear();
		_nearTransitionDesired.Clear();
		_mediumVolumetricVisualDesired.Clear();
		_coarseVolumetricVisualDesired.Clear();
		_collisionDesired.Clear();
		_editSubdivisionDesired.Clear();
		_residentBuildDesired.Clear();
		_ownedPresenters=0;
		ReadyRenderCount=0;
		ReadyCollisionCount=0;
		while(_ready.TryDequeue(out Result ignoredBeforeDrain)){}
		while(_nearTransitionReady.TryDequeue(out NearTransitionResult ignoredNearTransitionBeforeDrain)){}
		while(_mediumReady.TryDequeue(out MediumResult ignoredMediumBeforeDrain)){}
		while(_coarseReady.TryDequeue(out CoarseResult ignoredCoarseBeforeDrain)){}
		RingTerrainSampler? sampler=_sourceSampler; SmoothTerrainVolumetricDensitySnapshot? workerContexts=_cachedVolumetricDensitySnapshot; SemaphoreSlim workers=_workers; CancellationTokenSource rootCancel=_cancel;
		_=Task.WhenAll(pending).ContinueWith(_=>
		{
			// Workers can enqueue after the main-thread pre-drain, so this must run after WhenAll.
			while(_ready.TryDequeue(out Result ignoredAfterDrain)){}
			while(_nearTransitionReady.TryDequeue(out NearTransitionResult ignoredNearTransitionAfterDrain)){}
			while(_mediumReady.TryDequeue(out MediumResult ignoredMediumAfterDrain)){}
			while(_coarseReady.TryDequeue(out CoarseResult ignoredCoarseAfterDrain)){}
			foreach(var tier in _bridgeTiers)while(tier.Ready.TryDequeue(out BridgeResult _)){}
			lock(_lifetimeGate)
			{
				_trackedTasks.Clear();
				_tasks.Clear();
			}
			Metrics=Metrics with { Desired=0, VisualDesired=0, CollisionDesired=0, EditSubdivisionDesired=0, QueueDepth=0, Pending=0, ActiveWorkers=0, Resident=0, Ready=0, Pool=0, EstimatedBytes=0 };
			workerContexts?.DisposePooledWorkerContexts();
			sampler?.Dispose();
			workers.Dispose();
			rootCancel.Dispose();
			_shutdown.TrySetResult();
		}, TaskScheduler.Default);
	}
	private void OnGeometryChanged(RingLodGeometrySnapshot previous,RingLodGeometrySnapshot current,RingCanonicalPosition anchor)
	{
		_geometry=current;
		foreach(var e in _entries)
		{
			Vector3 origin=ChunkWorldOrigin(e.Key);
			if(e.Value.Presenter!=null)e.Value.Presenter.Position=origin;
			if(e.Value.Patches!=null)foreach(SmoothTerrainPresenter? patch in e.Value.Patches)if(patch!=null)patch.Position=origin;
		}
		foreach(var tier in _bridgeTiers)foreach(var e in tier.Entries)e.Value.Presenter.Position=ChunkWorldOrigin(e.Key);
		foreach(var e in _nearTransitionEntries)e.Value.Presenter.Position=ChunkWorldOrigin(e.Key);
		foreach(var e in _coarseEntries)e.Value.Presenter.Position=ChunkWorldOrigin(e.Key);
		foreach(var e in _mediumEntries)
			foreach(SmoothTerrainPresenter presenter in e.Value.Presenters)presenter.Position=MediumSectorWorldOrigin(e.Key);
		foreach(var e in _coarseEntries)
			e.Value.Presenter.Position=ChunkWorldOrigin(e.Key);
	}
	public void SetCanonicalFocus(RingCanonicalPosition focus)
	{
		// A surface destination preload must win over an in-flight explorer
		// position, including direct calls from the character controller.
		if(_sourceGrid?.TryGetStreamingCanonicalFocusOverride(out RingCanonicalPosition preload)==true)
			focus=preload;
		if (_fastTravelFocusSuspended)
			UpdateFocusWithoutStreaming(focus);
		else
			SetFocusIfCenterChanged(focus);
	}
	public ulong EditRevision => _editRevision;
	public ulong VisualCoverageRevision => _visualCoverageRevision;
	public ulong TerrainDefinitionHash => _baseline?.Revision ?? 0;
	public ulong LegacyTerrainDefinitionHash => _baseline?.LegacyRevision ?? 0;
	public ulong PreCavityLegacyTerrainDefinitionHash => _baseline?.PreCavityLegacyRevision ?? 0;
	/// <summary>Explicitly supported saved terrain identities, newest migration first.</summary>
	public ulong[] AcceptedLegacyTerrainDefinitionHashes => _baseline?.AcceptedLegacyRevisions ?? Array.Empty<ulong>();
	public double CircumferenceMeters => _baseline?.AroundLength ?? 0;
	/// <summary>The non-editable lower structural shell shared with the legacy terrain backend.</summary>
	public int StructuralBottomVertical => _sourceGrid?.GetStructuralBottomGlobalY() ?? int.MinValue;
	public int TerrainBottomVertical => _sourceGrid?.TerrainBottomGlobalY ?? int.MinValue;
	/// <summary>Captures the immutable authoritative field used by every cave LOD.
	/// It is cached by edit revision and never exposes the mutable grid state.</summary>
	public SmoothTerrainVolumetricDensitySnapshot? CaptureVolumetricDensitySnapshot()
	{
		SmoothTerrainEditSnapshot? edits=EnsureEditSnapshot();
		if (_baseline == null || edits == null) return null;
		if (_cachedVolumetricDensitySnapshot?.Identity.BaselineRevision == _baseline.Revision
			&& _cachedVolumetricDensitySnapshot.Identity.EditRevision == edits.Revision)
			return _cachedVolumetricDensitySnapshot;
		return _cachedVolumetricDensitySnapshot = new SmoothTerrainVolumetricDensitySnapshot(_baseline, edits);
	}
	private void RetireVolumetricDensitySnapshot()
	{
		if(_cachedVolumetricDensitySnapshot!=null)
			_cachedVolumetricDensitySnapshot.DisposePooledWorkerContexts();
		_cachedVolumetricDensitySnapshot=null;
	}
	private SmoothTerrainEditSnapshot? EnsureEditSnapshot()
	{
		if(_baseline==null)return null;
		if(_editSnapshot?.Revision!=_editRevision)
			_editSnapshot=new SmoothTerrainEditSnapshot(_commands,_editRevision,_baseline.AroundLength);
		return _editSnapshot;
	}
	public SmoothTerrainJournalEnvelope CaptureJournalEnvelope()=>new(SmoothTerrainEditJournalStore.FormatVersion,TerrainDefinitionHash,CircumferenceMeters,_editRevision,_nextCommandId,_nextSequence,_commands.ToArray());
	public bool RestoreJournalEnvelope(SmoothTerrainJournalEnvelope envelope)
	{
		if(_baseline==null)return false;try{SmoothTerrainEditJournalStore.ValidateEnvelope(envelope,TerrainDefinitionHash,CircumferenceMeters,_baseline.AcceptedLegacyRevisions);bool preCavity=envelope.TerrainDefinitionHash==_baseline.PreCavityLegacyRevision;foreach(var command in envelope.Commands)if(CrossesProtectedStructure(command)||(preCavity&&(double)command.Vertical-command.Radius<TerrainBottomVertical))throw new ArgumentOutOfRangeException(nameof(envelope));var snapshot=new SmoothTerrainEditSnapshot(envelope.Commands,envelope.Revision,_baseline.AroundLength);_commands.Clear();_editRevisionCommands.Clear();foreach(var c in snapshot.Ordered)_commands.Add(c);_editRevision=envelope.Revision;_nextCommandId=envelope.NextCommandId;_nextSequence=envelope.NextSequence;_editSnapshot=snapshot;_cachedLodSurfaceEditSnapshot=null;RetireVolumetricDensitySnapshot();}catch(Exception){return false;}ClearPendingEditRefreshState();foreach(var p in new List<KeyValuePair<SmoothTerrainChunkCoordinate,Entry>>(_entries))RemoveEntry(p.Key,p.Value);RefreshDesired();return true;
	}
	public void AddEdit(SmoothTerrainEditCommand command)
	{
		if(!command.IsValid || _baseline==null || command.Radius>SmoothTerrainEditSnapshot.MaximumStampRadiusMeters || command.Radius>_baseline.AroundLength*.5f || !Enum.IsDefined((SmoothTerrainMaterial)command.Material) || _commands.Exists(c=>c.CommandId==command.CommandId))
			throw new ArgumentException("Invalid smooth terrain edit.",nameof(command));
		// The smooth backend used to allow an excavation to pass beneath the
		// structural shell used by the rest of the ring.  That produced solid
		// density with no agreed streaming/physics policy below it.
		if(CrossesProtectedStructure(command))
			throw new ArgumentOutOfRangeException(nameof(command),"Smooth terrain edits may not cross a protected lower-hull or sidewall boundary.");
		command=command with { Around=RingCoordMath.WrapAround(command.Around,_baseline.AroundLength) };
		bool appendOrdered=_commands.Count==0
			|| command.Sequence>_commands[^1].Sequence
			|| (command.Sequence==_commands[^1].Sequence&&command.CommandId>_commands[^1].CommandId);
		_commands.Add(command); _editRevision++; _nextCommandId=Math.Max(_nextCommandId,checked(command.CommandId+1));_nextSequence=Math.Max(_nextSequence,checked(command.Sequence+1));_editSnapshot=null;RetireVolumetricDensitySnapshot();_cachedLodSurfaceEditSnapshot=null;
		var affected=new HashSet<SmoothTerrainChunkCoordinate>();
		foreach(var pair in _entries)
			if(StampOverlaps(pair.Key,command)) affected.Add(pair.Key);
		// Coalesce all edits issued in this process frame.  Scheduling every
		// intermediate journal revision made a rapid brush burst create and
		// cancel worker work that could never be installed.
		bool startingBatch=!_editRefreshQueued;
		if(startingBatch)
		{
			_pendingEditSnapshots.Clear();
			_pendingEditCommands.Clear();
			_pendingEditSnapshotsValid=appendOrdered;
		}
		else _pendingEditSnapshotsValid&=appendOrdered;
		if(_pendingEditSnapshotsValid)
		{
			_pendingEditCommands.Add(command);
			// Newly affected coordinates can start from their immutable
			// installed lattice because all earlier batch stamps were
			// non-overlapping. The complete batch is applied once at flush.
			foreach(SmoothTerrainChunkCoordinate coordinate in affected)
				if(!_pendingEditSnapshots.ContainsKey(coordinate)
					&&_entries.TryGetValue(coordinate,out Entry? entry)
					&&entry.TargetSnapshot is SmoothTerrainChunkSnapshot cached)
					_pendingEditSnapshots[coordinate]=cached;
		}
		else {_pendingEditSnapshots.Clear();_pendingEditCommands.Clear();}
		_pendingEditAffected.UnionWith(affected);
		foreach(SmoothTerrainChunkCoordinate coordinate in affected)
		{
			byte mask=PatchMaskForStamp(coordinate,command);
			_pendingEditPatchMasks[coordinate]=_pendingEditPatchMasks.TryGetValue(coordinate,out byte previousMask)
				?(byte)(previousMask|mask):mask;
		}
		_editRevisionCommands[_editRevision]=command;
		_editRefreshQueued=true;
	}
	private bool CrossesProtectedStructure(in SmoothTerrainEditCommand command)
	{
		if ((double)command.Vertical - command.Radius < StructuralBottomVertical)
			return true;
		if (_baseline == null || _sourceGrid == null)
			return false;
		double minimumVertical = command.Vertical - command.Radius;
		double maximumVertical = command.Vertical + command.Radius;
		if (maximumVertical < StructuralBottomVertical || minimumVertical > _sourceGrid.GetStructuralWallTopGlobalY())
			return false;
		// The left wall owns voxel centers [-32,-1], the right wall owns
		// [width,width+31]. Reject a stamp before it can modify either slab;
		// this applies equally to excavation and add-matter commands.
		double minimumAcross = command.Across - command.Radius;
		double maximumAcross = command.Across + command.Radius;
		int width = _baseline.AcrossWidth;
		return maximumAcross >= -SmoothTerrainChunkSnapshot.Cells
			&& minimumAcross <= -1
			|| maximumAcross >= width
			&& minimumAcross <= width + SmoothTerrainChunkSnapshot.Cells - 1;
	}
	public IReadOnlyList<SmoothTerrainEditCommand> CaptureEditCommands()=>_commands.ToArray();
	/// <summary>Read-only projection used by non-editable proxy LODs.</summary>
	public RingLodSurfaceEditSnapshot CaptureLodSurfaceEditSnapshot()
	{
		SmoothTerrainEditSnapshot? edits=EnsureEditSnapshot();
		if (_baseline == null || _sourceSampler == null || edits == null)
			return new RingLodSurfaceEditSnapshot(0, 1L, new Dictionary<RingTerrainColumnCoordinate, int>());
		if (_cachedLodSurfaceEditSnapshot?.Version == _editRevision)
			return _cachedLodSurfaceEditSnapshot;

		var bounds = new Dictionary<RingTerrainColumnCoordinate, (int Minimum, int Maximum)>();
		foreach (SmoothTerrainEditCommand stamp in _commands)
		{
			int radius = (int)Math.Ceiling(stamp.Radius);
			int minimumAcross = Math.Max(0, stamp.Across - radius);
			int maximumAcross = Math.Min(_baseline.AcrossWidth, stamp.Across + radius);
			for (int across = minimumAcross; across <= maximumAcross; across++)
			for (long rawAround = stamp.Around - radius; rawAround <= stamp.Around + radius; rawAround++)
			{
				long around = RingCoordMath.WrapAround(rawAround, _baseline.AroundLength);
				long dx = around - stamp.Around;
				dx %= _baseline.AroundLength;
				if (dx > _baseline.AroundLength / 2) dx -= _baseline.AroundLength;
				if (dx < -_baseline.AroundLength / 2) dx += _baseline.AroundLength;
				long dz = across - stamp.Across;
				double horizontalSquared = (double)dx * dx + (double)dz * dz;
				double radiusSquared = (double)stamp.Radius * stamp.Radius;
				if (horizontalSquared > radiusSquared) continue;

				int verticalExtent = (int)Math.Ceiling(Math.Sqrt(radiusSquared - horizontalSquared));
				int minimum = (int)Math.Max(int.MinValue, (long)stamp.Vertical - verticalExtent - 2L);
				int maximum = (int)Math.Min(int.MaxValue, (long)stamp.Vertical + verticalExtent + 2L);
				var key = new RingTerrainColumnCoordinate(around, across);
				if (bounds.TryGetValue(key, out var current))
					bounds[key] = (Math.Min(current.Minimum, minimum), Math.Max(current.Maximum, maximum));
				else
					bounds.Add(key, (minimum, maximum));
			}
		}

		var heights = new Dictionary<RingTerrainColumnCoordinate, int>();
		var dirty = new HashSet<RingTerrainColumnCoordinate>();
		var scratch = new List<SmoothTerrainEditCommand>();
		foreach ((RingTerrainColumnCoordinate column, (int Minimum, int Maximum) range) in bounds)
		{
			int baseline = _sourceSampler.GetSurfaceHeight(column.AroundVoxel, column.AcrossVoxel);
			int top = (int)Math.Min(int.MaxValue, Math.Max((long)baseline + 2L, range.Maximum));
			int bottom = (int)Math.Max(int.MinValue, Math.Min((long)baseline - 2L, range.Minimum));
			int surface = baseline;
			bool found = false;
			for (int vertical = top; ; vertical--)
			{
				if (edits.SampleContinuous(_baseline, column.AroundVoxel, vertical, column.AcrossVoxel, scratch).Density <= 0.0f)
				{
					surface = vertical;
					found = true;
					break;
				}
				if (vertical == bottom) break;
			}
			dirty.Add(column);
			if (found && surface != baseline) heights[column] = surface;
		}

		_cachedLodSurfaceEditSnapshot = new RingLodSurfaceEditSnapshot(
			_editRevision,
			_baseline.AroundLength,
			heights,
			dirty);
		return _cachedLodSurfaceEditSnapshot;
	}
	private RingLodSurfaceEditSnapshot ProjectAppendedEditToLod(RingLodSurfaceEditSnapshot previous,SmoothTerrainEditCommand stamp)
	{
		if(_baseline==null||_sourceSampler==null||_editSnapshot==null)
			return new RingLodSurfaceEditSnapshot(_editRevision,1L,new Dictionary<RingTerrainColumnCoordinate,int>());
		var heights=new Dictionary<RingTerrainColumnCoordinate,int>(previous.CanonicalEditedColumns);
		var dirty=new HashSet<RingTerrainColumnCoordinate>();
		var scratch=new List<SmoothTerrainEditCommand>();
		int radius=(int)Math.Ceiling(stamp.Radius);
		int minimumAcross=Math.Max(0,stamp.Across-radius);
		int maximumAcross=Math.Min(_baseline.AcrossWidth,stamp.Across+radius);
		double radiusSquared=(double)stamp.Radius*stamp.Radius;
		for(int across=minimumAcross;across<=maximumAcross;across++)
		for(long rawAround=stamp.Around-radius;rawAround<=stamp.Around+radius;rawAround++)
		{
			long around=RingCoordMath.WrapAround(rawAround,_baseline.AroundLength);
			long dx=Shortest(around,stamp.Around,_baseline.AroundLength);
			long dz=across-stamp.Across;
			double horizontalSquared=(double)dx*dx+(double)dz*dz;
			if(horizontalSquared>radiusSquared)continue;
			var column=new RingTerrainColumnCoordinate(around,across);
			if(!dirty.Add(column))continue;
			int baseline=_sourceSampler.GetSurfaceHeight(around,across);
			int prior=heights.TryGetValue(column,out int previousHeight)?previousHeight:baseline;
			int verticalExtent=(int)Math.Ceiling(Math.Sqrt(radiusSquared-horizontalSquared));
			int stampMinimum=(int)Math.Max(int.MinValue,(long)stamp.Vertical-verticalExtent-2L);
			int stampMaximum=(int)Math.Min(int.MaxValue,(long)stamp.Vertical+verticalExtent+2L);
			int top=(int)Math.Min(int.MaxValue,Math.Max(Math.Max((long)baseline+2L,(long)prior+2L),stampMaximum));
			int bottom=(int)Math.Max(int.MinValue,Math.Min(Math.Min((long)baseline-2L,(long)prior-2L),stampMinimum));
			int surface=baseline;
			bool found=false;
			for(int vertical=top;;vertical--)
			{
				if(_editSnapshot.SampleContinuous(_baseline,around,vertical,across,scratch).Density<=0.0f)
				{
					surface=vertical;
					found=true;
					break;
				}
				if(vertical==bottom)break;
			}
			if(found&&surface!=baseline)heights[column]=surface;
			else heights.Remove(column);
		}
		return new RingLodSurfaceEditSnapshot(_editRevision,_baseline.AroundLength,heights,dirty);
	}
	public bool IsSurfaceRegionVisualReady(long around0,long around1,int across0,int across1)
	{
		if (_baseline == null || _sourceSampler == null) return false;
		long aroundMinimum = Math.Min(around0, around1);
		long aroundMaximum = Math.Max(around0, around1);
		int acrossMinimum = Math.Max(0, Math.Min(across0, across1));
		int acrossMaximum = Math.Min(_baseline.AcrossWidth, Math.Max(across0, across1));
		if (aroundMaximum <= aroundMinimum || acrossMaximum <= acrossMinimum) return false;

		long firstAroundChunk = FloorDiv(aroundMinimum, SmoothTerrainChunkSnapshot.Cells);
		long lastAroundChunk = FloorDiv(aroundMaximum - 1, SmoothTerrainChunkSnapshot.Cells);
		int firstAcrossChunk = RingCoordMath.FloorDiv(acrossMinimum, SmoothTerrainChunkSnapshot.Cells);
		int lastAcrossChunk = RingCoordMath.FloorDiv(acrossMaximum - 1, SmoothTerrainChunkSnapshot.Cells);
		long aroundChunkCount = Math.Max(1, _baseline.AroundLength / SmoothTerrainChunkSnapshot.Cells);
		for (int acrossChunk = firstAcrossChunk; acrossChunk <= lastAcrossChunk; acrossChunk++)
		for (long rawAroundChunk = firstAroundChunk; rawAroundChunk <= lastAroundChunk; rawAroundChunk++)
		{
			long aroundChunk = RingCoordMath.WrapAroundChunk(rawAroundChunk, aroundChunkCount);
			GetColumnSurfaceChunkRange(aroundChunk,acrossChunk,out int minimumSurfaceChunk,out int maximumSurfaceChunk);
			bool hasRenderMesh=false;
			for(int verticalChunk=minimumSurfaceChunk;verticalChunk<=maximumSurfaceChunk;verticalChunk++)
			{
				var coordinate=new SmoothTerrainChunkCoordinate(aroundChunk,verticalChunk,acrossChunk);
				if(!_surfaceVisualDesired.Contains(coordinate)
					||!_entries.TryGetValue(coordinate,out Entry? entry)
					||!entry.Ready
					// Empty halo parents are valid.  What matters here is
					// that their active representation is fully installed:
					// one visible monolith or all eight visible hot patches.
					||!(entry.Presenter?.Visible==true
						||entry.Patches?.All(patch=>patch?.Visible==true)==true))
					return false;
				hasRenderMesh|=EntryHasRender(entry);
			}
			// Empty halo chunks can be ready, but they cannot by themselves
			// replace a proxy surface. At least one chunk in the complete
			// sampled surface span must actually own triangles.
			if(!hasRenderMesh)return false;
		}
		return true;
	}
	public bool IsSurfaceCollisionReady(long around,int across)
	{
		if(_baseline==null||_sourceSampler==null)return false;
		long canonicalAround=RingCoordMath.WrapAround(around,_baseline.AroundLength);
		int canonicalAcross=Math.Clamp(across,0,_baseline.AcrossWidth);
		var coordinate=new SmoothTerrainChunkCoordinate(
			canonicalAround/SmoothTerrainChunkSnapshot.Cells,
			GetSurfaceVerticalChunk(canonicalAround,canonicalAcross),
			RingCoordMath.FloorDiv(canonicalAcross,SmoothTerrainChunkSnapshot.Cells));
		return _entries.TryGetValue(coordinate,out Entry? entry)
			&& entry.Ready
			&& EntryHasCollision(entry);
	}
	public bool IsSurfaceCollisionNeighborhoodReady(long around,int across,int radiusChunks=1)
	{
		if(_baseline==null)return false;
		int radius=Math.Max(0,radiusChunks);
		long centerAround=RingCoordMath.WrapAround(around,_baseline.AroundLength)/SmoothTerrainChunkSnapshot.Cells;
		int centerAcross=RingCoordMath.FloorDiv(Math.Clamp(across,0,_baseline.AcrossWidth),SmoothTerrainChunkSnapshot.Cells);
		long aroundChunkCount=Math.Max(1,(_baseline.AroundLength+SmoothTerrainChunkSnapshot.Cells-1)/SmoothTerrainChunkSnapshot.Cells);
		for(int z=-radius;z<=radius;z++)
		for(int x=-radius;x<=radius;x++)
		{
			int acrossChunk=centerAcross+z;
			if(acrossChunk*SmoothTerrainChunkSnapshot.Cells>_baseline.AcrossWidth
				||(acrossChunk+1)*SmoothTerrainChunkSnapshot.Cells<0)continue;
			long aroundChunk=RingCoordMath.WrapAroundChunk(centerAround+x,aroundChunkCount);
			long sampleAround=aroundChunk*SmoothTerrainChunkSnapshot.Cells+SmoothTerrainChunkSnapshot.Cells/2;
			int sampleAcross=Math.Clamp(
				acrossChunk*SmoothTerrainChunkSnapshot.Cells+SmoothTerrainChunkSnapshot.Cells/2,
				0,
				_baseline.AcrossWidth);
			if(!IsSurfaceCollisionReady(sampleAround,sampleAcross))return false;
		}
		return true;
	}
	public bool IsMovementSurfaceCollisionReady(double around,double across,float bodyRadius)
	{
		int probe=Math.Max(1,(int)Math.Ceiling(Math.Max(0.0f,bodyRadius)+.1f));
		long centerAround=(long)Math.Floor(around);
		int centerAcross=(int)Math.Floor(across);
		return IsSurfaceCollisionReady(centerAround,centerAcross)
			&&IsSurfaceCollisionReady(centerAround-probe,centerAcross)
			&&IsSurfaceCollisionReady(centerAround+probe,centerAcross)
			&&IsSurfaceCollisionReady(centerAround,centerAcross-probe)
			&&IsSurfaceCollisionReady(centerAround,centerAcross+probe);
	}
	/// <summary>
	/// Returns whether the active terrain representation is resident at an
	/// actual 3-D player location.  This deliberately does not require
	/// triangles: an excavated shaft is correctly empty while the player is
	/// falling through it, but its surrounding chunks must remain installed.
	/// </summary>
	public bool IsTerrainResidentAt(long around,int vertical,int across)
	{
		if(_baseline==null)return false;
		if(vertical<StructuralBottomVertical)return false;
		// Never turn an attempted step beyond a structural sidewall into an
		// in-range probe by clamping it.  Movement readiness is a safety gate:
		// callers use false to freeze traversal until a real local collider is
		// resident, so accepting an exterior coordinate here could permit a
		// fall through an unloaded side boundary.
		if(across<0||across>_baseline.AcrossWidth)return false;
		long canonicalAround=RingCoordMath.WrapAround(around,_baseline.AroundLength);
		var coordinate=new SmoothTerrainChunkCoordinate(
			FloorDiv(canonicalAround,SmoothTerrainChunkSnapshot.Cells),
			RingCoordMath.FloorDiv(vertical,SmoothTerrainChunkSnapshot.Cells),
			RingCoordMath.FloorDiv(across,SmoothTerrainChunkSnapshot.Cells));
		return _collisionDesired.Contains(coordinate)
			&&_entries.TryGetValue(coordinate,out Entry? entry)
			&&entry.Ready;
	}
	/// <summary>Actual-position streaming readiness for movement through caves and shafts.</summary>
	public bool IsMovementTerrainResident(double around,double vertical,double across,float bodyRadius,float bodyHeight=0.0f)
	{
		int probe=Math.Max(1,(int)Math.Ceiling(Math.Max(0.0f,bodyRadius)+.1f));
		long centerAround=(long)Math.Floor(around);
		int centerVertical=(int)Math.Floor(vertical);
		int centerAcross=(int)Math.Floor(across);
		// The chunk containing the feet is insufficient at a parent boundary:
		// the marching-cubes cell that owns a floor at y=-96 begins at y=-97.
		// Keep the one-cell-below owner resident before the capsule reaches it.
		int below=(int)Math.Floor(vertical-Math.Max(.2f,bodyRadius)-.01f);
		int above=(int)Math.Floor(vertical+Math.Max(0.0f,bodyHeight)+.01f);
		return IsTerrainResidentAt(centerAround,centerVertical,centerAcross)
			&&IsTerrainResidentAt(centerAround,below,centerAcross)
			&&IsTerrainResidentAt(centerAround,above,centerAcross)
			&&IsTerrainResidentAt(centerAround-probe,centerVertical,centerAcross)
			&&IsTerrainResidentAt(centerAround+probe,centerVertical,centerAcross)
			&&IsTerrainResidentAt(centerAround,centerVertical,centerAcross-probe)
			&&IsTerrainResidentAt(centerAround,centerVertical,centerAcross+probe);
	}
	public bool ReplaceEditCommands(IEnumerable<SmoothTerrainEditCommand> commands,ulong revision)
	{ if(_baseline==null)return false;var copy=new List<SmoothTerrainEditCommand>(commands);try{var snap=new SmoothTerrainEditSnapshot(copy,revision,_baseline.AroundLength);ulong maxId=0;long maxSequence=0;foreach(var c in copy){if(c.CommandId>maxId)maxId=c.CommandId;if(c.Sequence>maxSequence)maxSequence=c.Sequence;}_commands.Clear();_editRevisionCommands.Clear();foreach(var c in snap.Ordered)_commands.Add(c);_editRevision=revision;_editSnapshot=snap;_cachedLodSurfaceEditSnapshot=null;RetireVolumetricDensitySnapshot();_nextCommandId=copy.Count==0?1:checked(maxId+1);_nextSequence=copy.Count==0?1:checked(maxSequence+1);}catch(Exception){return false;}ClearPendingEditRefreshState();foreach(var p in new List<KeyValuePair<SmoothTerrainChunkCoordinate,Entry>>(_entries))RemoveEntry(p.Key,p.Value);RefreshDesired();return true; }
	public bool TryApplyNaturalBrushAtWorldPosition(Vector3 worldPosition,bool addsMatter,float radius,SmoothTerrainMaterial material,out SmoothTerrainEditCommand command)
	{ command=default;if(_sourceGrid==null||!float.IsFinite(radius)||radius<=0||radius>SmoothTerrainEditSnapshot.MaximumStampRadiusMeters||!Enum.IsDefined(material)||_nextCommandId==ulong.MaxValue||_nextSequence==long.MaxValue)return false;var p=_sourceGrid.WorldToCanonicalRingPosition64(worldPosition);var candidate=new SmoothTerrainEditCommand(SmoothTerrainEditCommand.CurrentVersion,_nextCommandId,_nextSequence,addsMatter,(long)Math.Floor(p.AroundMeters),(int)Math.Floor(p.VerticalMeters),(int)Math.Floor(p.AcrossMeters),radius,(int)material);try{AddEdit(candidate);command=candidate;return true;}catch(ArgumentException){return false;}}
	/// <summary>Main-thread fallback raycast. Density is negative in solid, so its gradient points outward to air.</summary>
	public bool TryRaycastEditableSurface(Vector3 origin,Vector3 direction,float maxDistance,out Vector3 hitPosition,out Vector3 hitNormal)
	{hitPosition=default;hitNormal=Vector3.Up;if(_sourceGrid==null||_baseline==null||!origin.IsFinite()||!direction.IsFinite()||!float.IsFinite(maxDistance)||maxDistance<=0||maxDistance>1024||direction.LengthSquared()<1e-8f)return false;direction=direction.Normalized();int steps=Math.Min(4096,(int)Math.Ceiling(maxDistance/.25f));float prev=SampleWorld(origin);if(!float.IsFinite(prev))return false;for(int i=1;i<=steps;i++){float t=Math.Min(maxDistance,i*.25f),current=SampleWorld(origin+direction*t);if(!float.IsFinite(current))return false;if((prev<=0)!=(current<=0)){float lo=Math.Max(0,t-.25f),hi=t;for(int q=0;q<10;q++){float mid=(lo+hi)*.5f,midValue=SampleWorld(origin+direction*mid);if(!float.IsFinite(midValue))return false;if((SampleWorld(origin+direction*lo)<=0)==(midValue<=0))lo=mid;else hi=mid;}hitPosition=origin+direction*((lo+hi)*.5f);float e=.1f;Vector3 gradient=new(SampleWorld(hitPosition+Vector3.Right*e)-SampleWorld(hitPosition-Vector3.Right*e),SampleWorld(hitPosition+Vector3.Up*e)-SampleWorld(hitPosition-Vector3.Up*e),SampleWorld(hitPosition+Vector3.Back*e)-SampleWorld(hitPosition-Vector3.Back*e));
				// Central differences need their spacing normalization. The prior
				// unscaled 0.1 m samples made a unit density field look like a
				// 0.2-length gradient, then incorrectly failed the normal check.
				gradient/=2.0f*e;
				if(!gradient.IsFinite()||gradient.LengthSquared()<1e-8f)return false;hitNormal=gradient.Normalized();return hitPosition.IsFinite()&&hitNormal.IsFinite();}prev=current;}return false;}
	// Main-thread raycast helper: _queryScratch is intentionally reused and is not worker-safe.
	private float SampleWorld(Vector3 world){var p=_sourceGrid!.WorldToCanonicalRingPosition64(world);return EnsureEditSnapshot()!.SampleContinuous(_baseline!,p.AroundMeters,p.VerticalMeters,p.AcrossMeters,_queryScratch).Density;}
	/// <summary>Main-thread diagnostic/query helper using the same immutable baseline and edit snapshot as raycast.</summary>
	public bool TryQueryEditableDensityAtWorldPosition(Vector3 worldPosition,out float density){density=0;if(_sourceGrid==null||_baseline==null||!worldPosition.IsFinite())return false;density=SampleWorld(worldPosition);return float.IsFinite(density);}
	/// <summary>
	/// Main-thread canonical-density query.  Systems which already own a
	/// canonical coordinate (for example underground sky-access) must use this
	/// rather than converting through a floating-origin world transform.
	/// </summary>
	public bool TryQueryEditableDensityAtCanonicalPosition(
		double aroundMeters,
		double verticalMeters,
		double acrossMeters,
		out float density)
	{
		density = 0.0f;
		SmoothTerrainEditSnapshot? edits=EnsureEditSnapshot();
		if (_baseline == null || edits == null
			|| !double.IsFinite(aroundMeters)
			|| !double.IsFinite(verticalMeters)
			|| !double.IsFinite(acrossMeters)) return false;
		density = edits.SampleContinuous(
			_baseline, aroundMeters, verticalMeters, acrossMeters, _queryScratch).Density;
		return float.IsFinite(density);
	}
	public override void _Process(double delta)
	{
		using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Smooth terrain update (inclusive)");
		// Material uniforms and diagnostic counters do not need render-frame
		// frequency.  The former hot path also rescanned every resident and all
		// historical diagnostics every frame, making performance decay as the
		// player travelled.
		if((_materialElapsed+=delta)>=.1){_materialElapsed=0;UpdateTerrainMaterial();}
		UpdateFastTravelFocusSuspension(delta);
		if(_streamingFocus!=null&&(_focusElapsed+=delta)>=Math.Max(.01,StreamingUpdateIntervalSeconds))
		{
			_focusElapsed=0;
			StreamingFocusPollCount++;
			var p=_sourceGrid!.TryGetStreamingCanonicalFocusOverride(out RingCanonicalPosition preload)
				? preload
				: _streamingFocus is RingVoxelExplorerController explorer
				? explorer.CanonicalPosition
				: _sourceGrid!.WorldToCanonicalRingPosition64(_streamingFocus.GlobalPosition);
			if (_fastTravelFocusSuspended)
				UpdateFocusWithoutStreaming(p);
			else
				SetFocusIfCenterChanged(p);
		}
		if(_lastFocusCenter.HasValue&&(_lastRadiusAround!=RadiusAround
			||_lastRadiusAcross!=RadiusAcross
			||_lastRadiusVertical!=RadiusVertical
			||_lastCollisionRadius!=CollisionRadius
			||_lastFocusCenteredCollisionCoverage!=EnableFocusCenteredCollisionCoverage
			||_lastFocusCenteredVisualCoverage!=EnableFocusCenteredVisualCoverage
			||_lastMediumVolumetricVisualCoverage!=EnableMediumVolumetricVisualCoverage
			||_lastCoarseVolumetricVisualCoverage!=EnableCoarseVolumetricVisualCoverage
			||_lastBridgeShells!=EnableVolumetricBridgeShells
			||_lastFocusVisualRadiusAround!=FocusVisualRadiusAround
			||_lastFocusVisualRadiusAcross!=FocusVisualRadiusAcross
			||_lastFocusVisualRadiusVertical!=FocusVisualRadiusVertical
			||_lastMediumVisualRadiusAround!=MediumVisualRadiusAround
			||_lastMediumVisualRadiusAcross!=MediumVisualRadiusAcross
			||_lastMediumVisualRadiusVertical!=MediumVisualRadiusVertical
			||_lastCoarseVisualRadiusAround!=EffectiveCoarseVisualRadiusAround||_lastCoarseVisualRadiusAcross!=EffectiveCoarseVisualRadiusAcross||_lastCoarseVisualRadiusVertical!=EffectiveCoarseVisualRadiusVertical
			||_lastCoarseRetainMargin!=CoarseRetainMarginSectors
			||_lastCoarseActivationHysteresis!=CoarseActivationHysteresisMeters
			||_lastCoarseUndergroundActivationDepth!=CoarseUndergroundActivationDepthMeters
			||_lastHotChunkPatches!=EnableHotChunkPatches))RefreshDesired();
		if(_editRefreshQueued)FlushPendingEditRefresh();
		// A demotion schedules replacement mesh work.  Keep the established
		// window entirely stable while fast-travel intentionally defers focus
		// streaming; resume processing after the forced current-focus refresh.
		if (!_fastTravelFocusSuspended) ServiceHotPatchDemotions(delta);
		ServiceDeferredMeshWork();
		ApplyWorkerCountChange();
		long installStart=Stopwatch.GetTimestamp();
		_installFrameStart=installStart;
		int installLimit=Math.Max(1,EffectiveMaxInstallsPerFrame);
		int nearInstalls=InstallReady(installLimit);
		int coarseInstalls=InstallCoarseReady(Math.Max(0,installLimit-nearInstalls));
		int bridgeInstalls=InstallBridgeReady(Math.Max(0,installLimit-nearInstalls-coarseInstalls));
		int mediumInstalls=InstallMediumReady(Math.Max(0,installLimit-nearInstalls-coarseInstalls-bridgeInstalls));
		int transitionInstalls=InstallNearTransitionReady(Math.Max(0,installLimit-nearInstalls-coarseInstalls-bridgeInstalls-mediumInstalls));
		int aggregateInstalls=nearInstalls+coarseInstalls+bridgeInstalls+mediumInstalls+transitionInstalls;
		double aggregateMilliseconds=Stopwatch.GetElapsedTime(installStart).TotalMilliseconds;
		_lastAggregateInstallMilliseconds=aggregateMilliseconds;_maxAggregateInstallMilliseconds=Math.Max(_maxAggregateInstallMilliseconds,aggregateMilliseconds);
		_lastAggregateInstalls=aggregateInstalls;_maxAggregateInstalls=Math.Max(_maxAggregateInstalls,aggregateInstalls);
		if(aggregateInstalls>0&&aggregateMilliseconds>Math.Max(0.0,EffectiveInstallBudgetMilliseconds))_installBudgetExceededFrames++;
		if((_metricsElapsed+=delta)>=.25){_metricsElapsed=0;RecountReady();UpdateCurrentMetrics();}
	}
	private long _installFrameStart;
	/// <summary>True while this frame's shared install time budget (all tiers combined) has not been spent.</summary>
	private bool FrameInstallBudgetAvailable()=>Stopwatch.GetElapsedTime(_installFrameStart).TotalMilliseconds<Math.Max(0.0,EffectiveInstallBudgetMilliseconds);
	/// <summary>A mesh with no geometry (uniform solid/empty region) is pure bookkeeping and consumes neither install budget nor cap.</summary>
	private static bool IsBookkeepingMesh(SmoothTerrainMeshData mesh)=>mesh.Vertices.Length==0;
	private static bool IsBookkeepingSector(SmoothTerrainMeshData[] meshes){foreach(SmoothTerrainMeshData mesh in meshes)if(mesh.Vertices.Length!=0)return false;return true;}
	/// <summary>
	/// Applies a runtime MaxWorkers change to the live semaphore without replacing it (in-flight
	/// workers release to the same instance). Raising adds permits at once; lowering absorbs
	/// permits as running work completes, so it takes effect as current jobs finish.
	/// </summary>
	private void ApplyWorkerCountChange()
	{
		int desired=EffectiveMaxWorkers;
		if(desired==_appliedWorkerCount||_stopping)return;
		SemaphoreSlim workers=_workers;int delta=desired-_appliedWorkerCount;_appliedWorkerCount=desired;
		if(delta>0){workers.Release(delta);return;}
		CancellationToken token=_cancel.Token;
		for(int i=0;i<-delta;i++)_=Task.Run(async()=>{try{await workers.WaitAsync(token);}catch(OperationCanceledException){}catch(ObjectDisposedException){}});
	}
	private void FlushPendingEditRefresh()
	{
		if(!_editRefreshQueued)return;
		_editRefreshQueued=false;
		_urgentSnapshots.Clear();
		if(_pendingEditSnapshotsValid&&_baseline!=null&&_pendingEditCommands.Count>0)
			foreach(var pair in _pendingEditSnapshots)
				_urgentSnapshots[pair.Key]=SmoothTerrainSnapshotCapture.ApplyAppendedEdits(
					pair.Value,_baseline,_pendingEditCommands,_editRevision);
		_urgentRebuilds.Clear();_urgentRebuilds.UnionWith(_pendingEditAffected);
		_urgentPatchMasks.Clear();foreach(var pair in _pendingEditPatchMasks)_urgentPatchMasks[pair.Key]=pair.Value;
		_editBatch=_pendingEditAffected.Count==0?null:new EditBatch(_editRevision,_pendingEditAffected);
		_batchRetained.Clear();_batchRetained.UnionWith(_pendingEditAffected);
		_pendingEditAffected.Clear();_pendingEditPatchMasks.Clear();_pendingEditSnapshots.Clear();_pendingEditCommands.Clear();_pendingEditSnapshotsValid=true;
		RefreshDesired();
	}
	private void ClearPendingEditRefreshState()
	{
		_editRefreshQueued=false;
		_pendingEditAffected.Clear();
		_pendingEditPatchMasks.Clear();
		_pendingEditSnapshots.Clear();
		_pendingEditCommands.Clear();
		_pendingEditSnapshotsValid=true;
		_urgentRebuilds.Clear();
		_urgentSnapshots.Clear();
		_urgentPatchMasks.Clear();
		_batchRetained.Clear();
		_editBatch=null;
		_refreshAfterBatchCommit=false;
	}
	private void UpdateFastTravelFocusSuspension(double delta)
	{
		RingVoxelExplorerController? explorer = _streamingFocus as RingVoxelExplorerController;
		bool eligible = SuspendFocusRebuildsDuringFastTravel && explorer is { NoclipEnabled: true };
		float speed = explorer?.ActualTravelSpeedMetersPerSecond ?? 0.0f;
		bool next = false;
		if (eligible)
		{
			if (_fastTravelFocusSuspended)
			{
				if (speed <= Math.Max(0.0f, FastTravelStoppedSpeedThresholdMetersPerSecond))
				{
					_fastTravelStoppedElapsedSeconds += Math.Max(0.0, delta);
					next = _fastTravelStoppedElapsedSeconds < Math.Max(0.0f, FastTravelResumeStoppedSeconds);
				}
				else
				{
					_fastTravelStoppedElapsedSeconds = 0.0;
					next = true;
				}
			}
			else
			{
				_fastTravelStoppedElapsedSeconds = 0.0;
				next = speed >= Math.Max(100.0f, FastTravelSpeedThresholdMetersPerSecond);
			}
		}
		else
		{
			_fastTravelStoppedElapsedSeconds = 0.0;
		}
		if (next == _fastTravelFocusSuspended)
		{
			if (next) _fastTravelSuspendedElapsedSeconds += Math.Max(0.0, delta);
			return;
		}
		_fastTravelFocusSuspended = next;
		_fastTravelStoppedElapsedSeconds = 0.0;
		if (next)
		{
			GD.Print($"Smooth terrain focus rebuilds suspended at {speed:0}m/s; installed terrain remains visible.");
			return;
		}
		// The cached center is intentionally invalidated: the current canonical
		// focus may have crossed many chunks while streaming was suspended.
		_lastFocusCenter = null;
		_lastHotPatchAnchor = null;
		if (_sourceGrid?.TryGetStreamingCanonicalFocusOverride(out RingCanonicalPosition preload) == true)
			UpdateFocusWithoutStreaming(preload);
		else if (explorer != null)
			UpdateFocusWithoutStreaming(explorer.CanonicalPosition);
		FastTravelResumeRefreshCount++;
		RefreshDesired(force: true);
		GD.Print("Smooth terrain focus rebuilds resumed at the current canonical position.");
	}
	private enum MeshWorkTier { Base, NearTransition, Medium, Bridge, Coarse, CriticalBase }
	private sealed record DeferredMeshWork(CancellationToken Token, Action Start, Action Cancel);

	private void DeferMeshWork(MeshWorkTier tier, CancellationToken token, Action start, Action cancel)
	{
		lock (_lifetimeGate)
		{
			if (_stopping || token.IsCancellationRequested)
			{
				cancel();
				return;
			}
			_deferredWork[(int)tier].Enqueue(new DeferredMeshWork(token, start, cancel));
		}
	}

	private void ServiceDeferredMeshWork()
	{
		// A Task.Run waiting on _workers is still an allocated async state and
		// can race ahead of the collision-first FIFO before acquiring the
		// semaphore. Admit no more tasks than can actually execute now; the
		// deferred queues are the only waiting area.
		int capacity = Math.Max(1, Math.Min(MaxOutstandingMeshWork, EffectiveMaxWorkers));
		bool cavePreload = IsCavePreloadSchedulingActive();
		while (true)
		{
			DeferredMeshWork? next = null;
			lock (_lifetimeGate)
			{
				if (_trackedTasks.Count >= capacity) return;
				int availableMask = 0;
				for (int index = 0; index < _deferredWork.Length; index++)
				{
					Queue<DeferredMeshWork> queue = _deferredWork[index];
					while (queue.Count > 0 && queue.Peek().Token.IsCancellationRequested)
					{
						queue.Dequeue().Cancel();
						_deferredCancelled++;
					}
					if (queue.Count > 0) availableMask |= 1 << index;
				}
				int selected = SelectDeferredTierIndex(
					availableMask,
					_nextDeferredTier,
					cavePreload);
				if (selected >= 0)
				{
					next = _deferredWork[selected].Dequeue();
					_nextDeferredTier = (selected + 1) % _deferredWork.Length;
					_deferredSubmitted++;
				}
			}
			if (next == null) return;
			next.Start();
		}
	}

	private bool IsCavePreloadSchedulingActive() => EnableCavePreloadScheduling
		&& _baseline?.DesignedCavesEnabled == true
		&& IsStitchedCaveLodEnabled
		&& IsCoarseUndergroundFocus();

	/// <summary>
	/// Urgent edit/collision work is always first. Underground cave preloads
	/// then fill their coarse fallback before the ordinary round-robin resumes.
	/// This changes admission order only; every requested tier remains queued.
	/// </summary>
	private static int SelectDeferredTierIndex(int availableMask, int nextTier, bool cavePreload)
	{
		if (availableMask == 0) return -1;
		int critical = (int)MeshWorkTier.CriticalBase;
		if ((availableMask & (1 << critical)) != 0) return critical;
		int coarse = (int)MeshWorkTier.Coarse;
		if (cavePreload && (availableMask & (1 << coarse)) != 0) return coarse;
		int count = (int)MeshWorkTier.CriticalBase + 1;
		for (int offset = 0; offset < count; offset++)
		{
			int index = (nextTier + offset) % count;
			if ((availableMask & (1 << index)) != 0) return index;
		}
		return -1;
	}

	internal static bool CavePreloadPriorityRegressionTest(out string summary)
	{
		int baseTier = 1 << (int)MeshWorkTier.Base;
		int near = 1 << (int)MeshWorkTier.NearTransition;
		int coarse = 1 << (int)MeshWorkTier.Coarse;
		int critical = 1 << (int)MeshWorkTier.CriticalBase;
		int all = baseTier | near | coarse | critical;
		if (SelectDeferredTierIndex(all, 0, cavePreload: true) != (int)MeshWorkTier.CriticalBase
			|| SelectDeferredTierIndex(all & ~critical, 0, cavePreload: true) != (int)MeshWorkTier.Coarse
			|| SelectDeferredTierIndex(baseTier | near, 0, cavePreload: true) != (int)MeshWorkTier.Base
			|| SelectDeferredTierIndex(baseTier | coarse, 0, cavePreload: false) != (int)MeshWorkTier.Base
			|| SelectDeferredTierIndex(near, (int)MeshWorkTier.Medium, cavePreload: false) != (int)MeshWorkTier.NearTransition
			|| SelectDeferredTierIndex(0, 0, cavePreload: true) != -1)
		{
			summary = "critical/coarse cave preload admission order changed";
			return false;
		}
		summary = "collision/edit first, coarse cave shell second, fair refinement retained";
		return true;
	}

	private void TrackMeshWork(Task work)
	{
		lock (_lifetimeGate)
		{
			_trackedTasks.Add(work);
			_tasks.TryAdd(work, 0);
		}
		_ = work.ContinueWith(_ =>
		{
			lock (_lifetimeGate)
			{
				_trackedTasks.Remove(work);
				_tasks.TryRemove(work, out byte _);
			}
		}, TaskScheduler.Default);
	}
	public override void _PhysicsProcess(double delta)
	{
		// The replacement collision was installed before this queue is filled.
		// Retiring the old representation on the following physics tick keeps
		// broad-phase coverage continuous during promotion and demotion.
		while(_physicsDeferredReleases.Count>0)ReleasePresenter(_physicsDeferredReleases.Dequeue());
	}
	private void SetFocusIfCenterChanged(RingCanonicalPosition focus)
	{
		if(_baseline==null)return;
		long focusAround=RingCoordMath.WrapAround((long)Math.Floor(focus.AroundMeters),_baseline.AroundLength);
		long aroundChunk=focusAround/SmoothTerrainChunkSnapshot.Cells;
		int focusAcross=Math.Clamp((int)Math.Floor(focus.AcrossMeters),0,_baseline.AcrossWidth);
		int acrossChunk=RingCoordMath.FloorDiv(focusAcross,SmoothTerrainChunkSnapshot.Cells);
		// Focus-centred cave coverage follows the actual 3D parent, not the
		// surface parent. Surface loops calculate their own vertical spans.
		int verticalChunk=RingCoordMath.FloorDiv((int)Math.Floor(focus.VerticalMeters),SmoothTerrainChunkSnapshot.Cells);
		var center=new SmoothTerrainChunkCoordinate(aroundChunk,verticalChunk,acrossChunk);
		_focus=focus;
		bool wasSurfaceResident=_surfaceResidencyActive;
		UpdateSurfaceResidency(focus);
		bool coarseLatchChanged=UpdateCoarseUndergroundLatch(focus);
		if(wasSurfaceResident!=_surfaceResidencyActive||coarseLatchChanged)
		{
			_lastFocusCenter=null;
			_lastHotPatchAnchor=null;
		}
		SmoothTerrainChunkCoordinate? hotAnchor=EnableHotChunkPatches?GetHotPatchAnchor():null;
		if(_lastFocusCenter==center&&_lastHotPatchAnchor==hotAnchor)return;
		_lastFocusCenter=center;
		_lastHotPatchAnchor=hotAnchor;
		RefreshDesired();
	}
	private void UpdateFocusWithoutStreaming(RingCanonicalPosition focus)
	{
		_focus = focus;
		UpdateSurfaceResidency(focus);
		UpdateCoarseUndergroundLatch(focus);
	}
	public void RefreshDesired(bool force = false)
	{
		if (_fastTravelFocusSuspended && !force)
		{
			FastTravelDeferredRefreshCount++;
			return;
		}
		if (_baseline == null) return;
		UpdateCoarseUndergroundLatch(_focus);
		_generation++; ulong generation = _generation;
		_lastRadiusAround=RadiusAround;_lastRadiusAcross=RadiusAcross;_lastRadiusVertical=RadiusVertical;_lastCollisionRadius=CollisionRadius;
		_lastFocusCenteredCollisionCoverage=EnableFocusCenteredCollisionCoverage;_lastHotChunkPatches=EnableHotChunkPatches;
		_lastFocusCenteredVisualCoverage=EnableFocusCenteredVisualCoverage;
		_lastMediumVolumetricVisualCoverage=EnableMediumVolumetricVisualCoverage;
		_lastCoarseVolumetricVisualCoverage=EnableCoarseVolumetricVisualCoverage;
		_lastBridgeShells=EnableVolumetricBridgeShells;
		_lastFocusVisualRadiusAround=FocusVisualRadiusAround;_lastFocusVisualRadiusAcross=FocusVisualRadiusAcross;_lastFocusVisualRadiusVertical=FocusVisualRadiusVertical;
		_lastMediumVisualRadiusAround=MediumVisualRadiusAround;_lastMediumVisualRadiusAcross=MediumVisualRadiusAcross;_lastMediumVisualRadiusVertical=MediumVisualRadiusVertical;
		_lastCoarseVisualRadiusAround=EffectiveCoarseVisualRadiusAround;_lastCoarseVisualRadiusAcross=EffectiveCoarseVisualRadiusAcross;_lastCoarseVisualRadiusVertical=EffectiveCoarseVisualRadiusVertical;_lastCoarseUndergroundActivationDepth=CoarseUndergroundActivationDepthMeters;
		_lastCoarseRetainMargin=CoarseRetainMarginSectors;_lastCoarseActivationHysteresis=CoarseActivationHysteresisMeters;
		// Deep focus remains resident because its altitude is negative; true
		// space focus must still release every 1 m representation.
		if(EnableAltitudeAwareResidency&&!_surfaceResidencyActive)
		{
			SuspendSurfacePresentation();
			return;
		}
		var edits = EnsureEditSnapshot() ?? throw new InvalidOperationException("Smooth terrain edit snapshot is unavailable.");
		SmoothTerrainVolumetricDensitySnapshot field=CaptureVolumetricDensitySnapshot()
			?? throw new InvalidOperationException("Authoritative volumetric density snapshot is unavailable.");
		long focusAround = RingCoordMath.WrapAround((long)Math.Floor(_focus.AroundMeters), _baseline.AroundLength);
		int focusAcross = Math.Clamp((int)Math.Floor(_focus.AcrossMeters), 0, _baseline.AcrossWidth);
		long centerAround = focusAround / SmoothTerrainChunkSnapshot.Cells;
		int centerAcross = RingCoordMath.FloorDiv(focusAcross, SmoothTerrainChunkSnapshot.Cells);
		long aroundChunkCount=Math.Max(1,(_baseline.AroundLength+SmoothTerrainChunkSnapshot.Cells-1)/SmoothTerrainChunkSnapshot.Cells);
		var desired = new HashSet<SmoothTerrainChunkCoordinate>();
		_surfaceVisualDesired.Clear();
		_nearVolumetricVisualDesired.Clear();
		_nearTransitionDesired.Clear();
		_mediumVolumetricVisualDesired.Clear();
		_visualDesired.Clear();
		_collisionDesired.Clear();
		_editSubdivisionDesired.Clear();
		CoverageOwnershipContract ownership = CaptureCoverageOwnershipContract();
		if (!ownership.IsValid) throw new InvalidOperationException("Smooth terrain coverage ownership configuration is invalid.");
		// Visual streaming is strictly controlled by visual radii.  Collision
		// is added below in a separate pass so a larger collision bubble is
		// never silently clipped by this visual window.
		if (_surfaceResidencyActive)
		for(int z=-ownership.VisualRadiusAcross;z<=ownership.VisualRadiusAcross;z++)
		for(long x=-ownership.VisualRadiusAround;x<=ownership.VisualRadiusAround;x++)
		{
			long aroundChunk=RingCoordMath.WrapAroundChunk(centerAround+x,aroundChunkCount);
			int acrossChunk=centerAcross+z;
			if(acrossChunk*SmoothTerrainChunkSnapshot.Cells>_baseline.AcrossWidth
				||(acrossChunk+1)*SmoothTerrainChunkSnapshot.Cells<0)continue;
			GetColumnSurfaceChunkRange(aroundChunk,acrossChunk,out int minimumSurfaceChunk,out int maximumSurfaceChunk);
			for(int y=minimumSurfaceChunk-ownership.VisualRadiusVertical;y<=maximumSurfaceChunk+ownership.VisualRadiusVertical;y++)
			{
				var coordinate=new SmoothTerrainChunkCoordinate(aroundChunk,y,acrossChunk);
				_surfaceVisualDesired.Add(coordinate);
				// The left wall's exterior face falls on the low chunk edge,
				// so its owning marching cell is in one extra exterior halo
				// parent. Keep that parent resident with the slab itself.
				if(acrossChunk==-1)
					_surfaceVisualDesired.Add(new SmoothTerrainChunkCoordinate(aroundChunk,y,-2));
			}
		}
		// Full-resolution cave visibility is centred on the actual 3D focus,
		// independent of the surface window, collision bubble, and 2x2x2 edit topology.
		if (ownership.FocusCenteredVisualEnabled)
		{
			long visualAround=RingCoordMath.WrapAround((long)Math.Floor(_focus.AroundMeters),_baseline.AroundLength);
			int visualAcross=Math.Clamp((int)Math.Floor(_focus.AcrossMeters),0,_baseline.AcrossWidth);
			int visualVertical=(int)Math.Floor(_focus.VerticalMeters);
			bool stitchedClipmap=IsStitchedCaveLodEnabled;
			if(stitchedClipmap)
			{
				// Fixed two-parent (four child) clipmap. Around stays
				// locally unwrapped so its transition lattice stays adjacent
				// at the ring seam; sampling and geometry wrap themselves.
				long FirstParent(long value) { long parent=FloorDiv(value,64); return value-parent*64<32 ? parent-1 : parent; }
				long startAround=FirstParent(visualAround)*2; int startAcross=checked((int)(FirstParent(visualAcross)*2)); int startVertical=checked((int)(FirstParent(visualVertical)*2));
				for(int z=0;z<4;z++)for(long x=0;x<4;x++)for(int y=0;y<4;y++)
				{ int acrossChunk=startAcross+z;if(acrossChunk*32>_baseline.AcrossWidth||(acrossChunk+1)*32<0)continue;long aroundChunk=startAround+x;if((long)(startVertical+y)*32<StructuralBottomVertical)continue;_nearVolumetricVisualDesired.Add(new SmoothTerrainChunkCoordinate(aroundChunk,startVertical+y,acrossChunk));if(acrossChunk==-1)_nearVolumetricVisualDesired.Add(new SmoothTerrainChunkCoordinate(aroundChunk,startVertical+y,-2)); }
			}
			else
			{
				// Standalone near tier remains radius-configurable.
				long aroundChunk=visualAround/SmoothTerrainChunkSnapshot.Cells; int acrossChunk=RingCoordMath.FloorDiv(visualAcross,SmoothTerrainChunkSnapshot.Cells); int verticalChunk=RingCoordMath.FloorDiv(visualVertical,SmoothTerrainChunkSnapshot.Cells);
				for(int z=-ownership.FocusVisualRadiusAcross;z<=ownership.FocusVisualRadiusAcross;z++)for(long x=-ownership.FocusVisualRadiusAround;x<=ownership.FocusVisualRadiusAround;x++)for(int y=-ownership.FocusVisualRadiusVertical-1;y<=ownership.FocusVisualRadiusVertical;y++)
				{ int childAcross=acrossChunk+z;if(childAcross*32>_baseline.AcrossWidth||(childAcross+1)*32<0)continue;long childAround=RingCoordMath.WrapAroundChunk(aroundChunk+x,aroundChunkCount);if((long)(verticalChunk+y)*32<StructuralBottomVertical)continue;_nearVolumetricVisualDesired.Add(new SmoothTerrainChunkCoordinate(childAround,verticalChunk+y,childAcross));if(childAcross==-1)_nearVolumetricVisualDesired.Add(new SmoothTerrainChunkCoordinate(childAround,verticalChunk+y,-2)); }
			}
		}
		_visualDesired.UnionWith(_surfaceVisualDesired);
		_visualDesired.UnionWith(_nearVolumetricVisualDesired);
		// Near physics has an independent, symmetric horizontal/vertical
		// envelope. It may intentionally exceed visual coverage (for example
		// while coarse terrain proxies own the visible distance).
		for(int z=-ownership.CollisionRadius;z<=ownership.CollisionRadius;z++)
		for(long x=-ownership.CollisionRadius;x<=ownership.CollisionRadius;x++)
		{
			long aroundChunk=RingCoordMath.WrapAroundChunk(centerAround+x,aroundChunkCount);
			int acrossChunk=centerAcross+z;
			if(acrossChunk*SmoothTerrainChunkSnapshot.Cells>_baseline.AcrossWidth
				||(acrossChunk+1)*SmoothTerrainChunkSnapshot.Cells<0)continue;
			GetColumnSurfaceChunkRange(aroundChunk,acrossChunk,out int minimumSurfaceChunk,out int maximumSurfaceChunk);
			for(int y=minimumSurfaceChunk-ownership.CollisionRadius;y<=maximumSurfaceChunk+ownership.CollisionRadius;y++)
			{
				var coordinate=new SmoothTerrainChunkCoordinate(aroundChunk,y,acrossChunk);
				_collisionDesired.Add(coordinate);
				if(acrossChunk==-1)
					_collisionDesired.Add(new SmoothTerrainChunkCoordinate(aroundChunk,y,-2));
			}
		}
		// Surface coverage alone is not adequate inside a cave: the player can
		// be many parent slabs below it. Keep a focus-centred near-physics
		// column independently from the edit 16 m bubble. The one parent
		// below is the marching-cell owner for a floor at a parent boundary.
		if (ownership.FocusCenteredCollisionEnabled)
		{
		long focusCollisionAround = RingCoordMath.WrapAround((long)Math.Floor(_focus.AroundMeters), _baseline.AroundLength)
			/ SmoothTerrainChunkSnapshot.Cells;
		int focusCollisionAcross = RingCoordMath.FloorDiv(
			Math.Clamp((int)Math.Floor(_focus.AcrossMeters), 0, _baseline.AcrossWidth),
			SmoothTerrainChunkSnapshot.Cells);
		int focusCollisionVertical = RingCoordMath.FloorDiv((int)Math.Floor(_focus.VerticalMeters), SmoothTerrainChunkSnapshot.Cells);
		for (int z = -ownership.CollisionRadius; z <= ownership.CollisionRadius; z++)
		for (long x = -ownership.CollisionRadius; x <= ownership.CollisionRadius; x++)
		for (int y = -ownership.CollisionRadius - 1; y <= ownership.CollisionRadius; y++)
		{
			int acrossChunk = focusCollisionAcross + z;
			if (acrossChunk * SmoothTerrainChunkSnapshot.Cells > _baseline.AcrossWidth
				|| (acrossChunk + 1) * SmoothTerrainChunkSnapshot.Cells < 0) continue;
			long aroundChunk = RingCoordMath.WrapAroundChunk(focusCollisionAround + x, aroundChunkCount);
			if ((long)(focusCollisionVertical+y)*SmoothTerrainChunkSnapshot.Cells < StructuralBottomVertical) continue;
			_collisionDesired.Add(new SmoothTerrainChunkCoordinate(aroundChunk, focusCollisionVertical + y, acrossChunk));
			if (acrossChunk == -1)
				_collisionDesired.Add(new SmoothTerrainChunkCoordinate(aroundChunk, focusCollisionVertical + y, -2));
		}
		}
		// The hot representation is an exact 2 x 2 x 2 parent cube, not an
		// incidental subset of the variable-height streaming window.  Ensure
		// every slot is resident before promotion. Collision ownership remains
		// the independent near-physics set above.
		foreach(SmoothTerrainChunkCoordinate hot in EnumerateHotPatchCoordinates())
		{
			_editSubdivisionDesired.Add(hot);
		}
		desired.UnionWith(_visualDesired);
		desired.UnionWith(_collisionDesired);
		desired.UnionWith(_editSubdivisionDesired);
		desired.UnionWith(_batchRetained);
		_residentBuildDesired.Clear();
		_residentBuildDesired.UnionWith(desired);
		foreach(var pair in new List<KeyValuePair<SmoothTerrainChunkCoordinate,Entry>>(_entries)) if(!desired.Contains(pair.Key)) RemoveEntry(pair.Key,pair.Value);
		if(ApplyVisualOwnership())BumpVisualCoverageRevision();
		ApplyCollisionDesired();
		// A HashSet's traversal order is not a streaming policy.  Submit the
		// exact collision window first, then spiral outward by chunk distance,
		// so the player gets a physical surface before the outer visual shell.
		foreach(var c in desired.OrderBy(c=>_urgentRebuilds.Contains(c)?0:_collisionDesired.Contains(c)?1:2).ThenBy(ChunkDistanceToFocus))
			if(!_entries.TryGetValue(c,out Entry? existing) || _urgentRebuilds.Contains(c) || (!existing.HotPatches && IsHotPatchCoordinate(c)))
			{
				byte mask=_urgentPatchMasks.TryGetValue(c,out byte urgentMask)?urgentMask:(byte)0xff;
				if(!_entries.TryGetValue(c,out Entry? current)||!current.HotPatches||current.Patches==null)mask=0xff;
				Schedule(c,generation,edits,field,_urgentSnapshots.TryGetValue(c,out SmoothTerrainChunkSnapshot? snapshot)?snapshot:null,mask);
			}
		// Near collision work was submitted first. Build the render hierarchy
		// from its safe outer fallback inward so a finer child cannot hide a
		// parent which has not been installed yet.
		RefreshCoarseVolumetricDesired(field);
		RefreshBridgeVolumetricDesired(field);
		RefreshMediumVolumetricDesired(field);
		RefreshNearTransitionDesired(field);
		PruneEditRevisionHistory();
		_urgentRebuilds.Clear();
		_urgentSnapshots.Clear();
		_urgentPatchMasks.Clear();
		PruneHistoricalDiagnostics();
		RecountReady(); UpdateCurrentMetrics(desired.Count);
	}
	private void RefreshMediumVolumetricDesired(SmoothTerrainVolumetricDensitySnapshot field)
	{
		if(!EnableMediumVolumetricVisualCoverage){ClearMediumVolumetricPresentation();return;}
		const int sectorMeters=64;
		long aroundMeters=RingCoordMath.WrapAround((long)Math.Floor(_focus.AroundMeters),_baseline!.AroundLength);
		int acrossMeters=Math.Clamp((int)Math.Floor(_focus.AcrossMeters),0,_baseline.AcrossWidth);
		bool stitchedClipmap=IsStitchedCaveLodEnabled;
		long ParentStart(long v){long p=FloorDiv(v,128);return v-p*128<64?p-1:p;}
		long centerAroundSector=stitchedClipmap?ParentStart(aroundMeters)*2:FloorDiv(aroundMeters,64);
		long centerAcrossSector=stitchedClipmap?ParentStart(acrossMeters)*2:RingCoordMath.FloorDiv(acrossMeters,64);
		long centerVerticalSector=stitchedClipmap?ParentStart((int)Math.Floor(_focus.VerticalMeters))*2:RingCoordMath.FloorDiv((int)Math.Floor(_focus.VerticalMeters),64);
		var wanted=new HashSet<SmoothTerrainChunkCoordinate>();
		int minZ=stitchedClipmap?0:-Math.Max(0,MediumVisualRadiusAcross), maxZ=stitchedClipmap?3:Math.Max(0,MediumVisualRadiusAcross);
		long minX=stitchedClipmap?0:-Math.Max(0,MediumVisualRadiusAround), maxX=stitchedClipmap?3:Math.Max(0,MediumVisualRadiusAround);
		int minY=stitchedClipmap?0:-Math.Max(0,MediumVisualRadiusVertical)-1, maxY=stitchedClipmap?3:Math.Max(0,MediumVisualRadiusVertical);
		for(int z=minZ;z<=maxZ;z++)
		for(long x=minX;x<=maxX;x++)
		for(int y=minY;y<=maxY;y++)
		{
			long sectorAround=centerAroundSector+x;
			int sectorAcross=checked((int)(centerAcrossSector+z)),sectorVertical=checked((int)(centerVerticalSector+y));
			int originAcross=sectorAcross*sectorMeters,originVertical=sectorVertical*sectorMeters;
			if(originAcross>_baseline.AcrossWidth||originAcross+sectorMeters<0||originVertical+sectorMeters<StructuralBottomVertical)continue;
			// Coordinates retain 32 m parent units so the established curved
			// transform can be reused; all medium sector origins are even.
			var coordinate=new SmoothTerrainChunkCoordinate(sectorAround*2,sectorVertical*2,sectorAcross*2);
			// Keep the sector as a fallback. Its 32 m presentation pieces
			// independently yield to ready surface or near-volume children.
			wanted.Add(coordinate);
			if(sectorAcross==0)wanted.Add(new SmoothTerrainChunkCoordinate(sectorAround*2,sectorVertical*2,-2));
		}
		_mediumVolumetricVisualDesired.Clear();
		_mediumVolumetricVisualDesired.UnionWith(wanted);
		foreach(var pair in _mediumEntries.ToArray())if(!wanted.Contains(pair.Key))RemoveMediumEntry(pair.Key,pair.Value);
		foreach(SmoothTerrainChunkCoordinate c in wanted)
			if(!_mediumEntries.TryGetValue(c,out MediumEntry? current))ScheduleMedium(c,field);
			else if(current.Identity!=field.Identity)
			{
				if(HasOverlappingEditSince(current.CheckedThroughEditRevision,c,2))
				{ RemoveMediumEntry(c,current); ScheduleMedium(c,field); }
				else current.CheckedThroughEditRevision=_editRevision;
			}
		ApplyMediumVisualOwnership();
	}
	private bool TryGetMediumChildIndex(SmoothTerrainChunkCoordinate sector,SmoothTerrainChunkCoordinate child,out int index)
	{
		long count=Math.Max(1,(_baseline!.AroundLength+31)/32);
		long x=RingCoordMath.WrapAroundChunk(child.AroundChunk-sector.AroundChunk,count);
		long y=(long)child.VerticalChunk-sector.VerticalChunk,z=(long)child.AcrossChunk-sector.AcrossChunk;
		index=0;
		if(x>=2||y<0||y>=2||z<0||z>=2)return false;
		index=(int)(x+2*(y+2*z));return true;
	}
	private SmoothTerrainChunkCoordinate MediumChildCoordinate(SmoothTerrainChunkCoordinate sector,int index)
		=>new(RingCoordMath.WrapAroundChunk(sector.AroundChunk+(index&1),Math.Max(1,(_baseline!.AroundLength+31)/32)),
			sector.VerticalChunk+((index>>1)&1),sector.AcrossChunk+((index>>2)&1));
	// A fine parent is allowed to replace only its matching medium child.  The
	// base presenter (or every active hot-patch tile) must already be visible;
	// this makes cancellation and edit rebuilds fall back to medium instead of
	// creating a transient hole.
	private static bool HasCompleteFineVisualPresentation(Entry entry)
		=>entry.Presenter?.Visible==true
		  ||(entry.Patches is { Length: 8 } patches
			 // A patch may legitimately be empty after an edit.  Its presenter
			 // is still the installed, ready owner; requiring a render mesh
			 // would keep the medium child underneath its seven siblings.
			 &&patches.All(patch=>patch?.Visible==true));
	private bool IsFineChildReadyVisualOwner(SmoothTerrainChunkCoordinate child)
		=>(_surfaceVisualDesired.Contains(child)||_nearVolumetricVisualDesired.Contains(child))
		  &&_entries.TryGetValue(child,out Entry? entry)&&entry.Ready
		  &&HasCompleteFineVisualPresentation(entry);
	private int CountVisibleMediumFineOverlaps(SmoothTerrainChunkCoordinate sector,MediumEntry entry)
	{int overlaps=0;for(int i=0;i<8;i++)if(entry.Presenters[i].Visible&&IsFineChildReadyVisualOwner(MediumChildCoordinate(sector,i)))overlaps++;return overlaps;}
	private void RefreshNearTransitionDesired(SmoothTerrainVolumetricDensitySnapshot field)
	{
		if(!IsStitchedCaveLodEnabled)
		{ ClearNearTransitionPresentation(); return; }
		var wanted=new HashSet<SmoothTerrainChunkCoordinate>();
		foreach(var fine in _nearVolumetricVisualDesired)
			if(CaptureNearTransitionMask(fine)!=0) wanted.Add(fine);
		_nearTransitionDesired.Clear();_nearTransitionDesired.UnionWith(wanted);
		foreach(var pair in _nearTransitionEntries.ToArray())if(!wanted.Contains(pair.Key))RemoveNearTransitionEntry(pair.Key,pair.Value);
		foreach(var fine in wanted)
			if(!_nearTransitionEntries.TryGetValue(fine,out NearTransitionEntry? entry))ScheduleNearTransition(fine,field);
			else
			{
				bool overlaps=entry.Identity!=field.Identity
					&&HasOverlappingEditSince(entry.CheckedThroughEditRevision,fine,1);
				if(overlaps||entry.FaceMask!=CaptureNearTransitionMask(fine))
				{ RemoveNearTransitionEntry(fine,entry); ScheduleNearTransition(fine,field); }
				else if(entry.Identity!=field.Identity)entry.CheckedThroughEditRevision=_editRevision;
			}
		ApplyNearTransitionOwnership();
	}
	// Faces on the outer fine envelope meet the medium parent. Interior fine
	// neighbours continue to use the ordinary 1 m marching mesh seam.
	private byte CaptureNearTransitionMask(SmoothTerrainChunkCoordinate c)
	{ byte mask=0;
	  if(!_nearVolumetricVisualDesired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk-1,c.VerticalChunk,c.AcrossChunk)))mask|=1;
	  if(!_nearVolumetricVisualDesired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk+1,c.VerticalChunk,c.AcrossChunk)))mask|=2;
	  if(!_nearVolumetricVisualDesired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk,c.VerticalChunk-1,c.AcrossChunk)))mask|=4;
	  if(!_nearVolumetricVisualDesired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk,c.VerticalChunk+1,c.AcrossChunk)))mask|=8;
	  if(!_nearVolumetricVisualDesired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk,c.VerticalChunk,c.AcrossChunk-1)))mask|=16;
	  if(!_nearVolumetricVisualDesired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk,c.VerticalChunk,c.AcrossChunk+1)))mask|=32;
	  return mask; }
	private void ScheduleNearTransition(SmoothTerrainChunkCoordinate fine,SmoothTerrainVolumetricDensitySnapshot field)
	{ if(_stopping)return; var cancel=CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);var presenter=AcquireNearTransitionPresenter();presenter.Visible=false;
	  byte mask=CaptureNearTransitionMask(fine);var entry=new NearTransitionEntry(_generation,field.Identity,cancel,presenter,mask);_nearTransitionEntries.Add(fine,entry);
	  RingLodGeometrySnapshot geometry=_geometry;Transform3D inverse=GlobalTransform.AffineInverse();
	  Task work;DeferMeshWork(MeshWorkTier.NearTransition,entry.Token,()=>{work=Task.Run(async()=>{bool acquired=false;try{await _workers.WaitAsync(entry.Token);acquired=true;entry.Token.ThrowIfCancellationRequested();
		  var origin=new RingTerrainSampleCoordinate(fine.AroundChunk*32,fine.VerticalChunk*32,fine.AcrossChunk*32);
		  var mediumOrigin=new RingTerrainSampleCoordinate(FloorDiv(origin.AroundVoxel,64)*64,(int)(RingCoordMath.FloorDiv(origin.VerticalVoxel,64)*64),(int)(RingCoordMath.FloorDiv(origin.AcrossVoxel,64)*64));
		  using SmoothTerrainVolumetricWorkerContext workerContext=field.CreateWorkerContext(UseIndependentWorkerSamplers);
		  SmoothTerrainMeshData mesh=TransformWorker(SmoothTerrainTransitionMesher.BuildFaces(workerContext.Field,origin,1,mediumOrigin,mask),fine,geometry,inverse,workerContext.Sampler??_sourceSampler);
		  entry.Token.ThrowIfCancellationRequested();_nearTransitionReady.Enqueue(new NearTransitionResult(fine,entry.Generation,field.Identity,mask,mesh));
	  }catch(OperationCanceledException){}finally{if(acquired)_workers.Release();entry.CompleteWorkAndDisposeToken();}},CancellationToken.None);TrackMeshWork(work);},entry.CompleteWorkAndDisposeToken); }
	private int InstallNearTransitionReady(int limit)
	{ if(SuspendInstallsForTests)return 0;int installed=0;while(((installed<limit&&FrameInstallBudgetAvailable())||(_nearTransitionReady.TryPeek(out NearTransitionResult peeked)&&IsBookkeepingMesh(peeked.Mesh)))&&_nearTransitionReady.TryDequeue(out NearTransitionResult result)){
		if(!_nearTransitionEntries.TryGetValue(result.Coordinate,out NearTransitionEntry? entry)||entry.Generation!=result.Generation||entry.Identity!=result.Identity||entry.FaceMask!=result.FaceMask)continue;
		entry.Ready=true;entry.Presenter.Position=ChunkWorldOrigin(result.Coordinate);entry.Presenter.Install(result.Mesh,false,_terrainMaterial);if(!IsBookkeepingMesh(result.Mesh))installed++;}
	  ApplyNearTransitionOwnership();return installed; }
	private void ApplyNearTransitionOwnership()
	{ foreach(var pair in _nearTransitionEntries)pair.Value.Presenter.Visible=_nearTransitionDesired.Contains(pair.Key)&&pair.Value.Ready&&IsVisualRepresentationVisibleForTests(pair.Key); }
	private SmoothTerrainPresenter AcquireNearTransitionPresenter(){if(_nearTransitionPool.Count>0)return _nearTransitionPool.Dequeue();var presenter=new SmoothTerrainPresenter();AddChild(presenter);return presenter;}
	private void RemoveNearTransitionEntry(SmoothTerrainChunkCoordinate coordinate,NearTransitionEntry entry){if(entry.RequestCancel())Interlocked.Increment(ref _auxiliaryCancelRequestCount);entry.Presenter.Reset();_nearTransitionPool.Enqueue(entry.Presenter);_nearTransitionEntries.Remove(coordinate);}
	private void ClearNearTransitionPresentation(){foreach(var pair in _nearTransitionEntries.ToArray())RemoveNearTransitionEntry(pair.Key,pair.Value);_nearTransitionDesired.Clear();}
	private void ApplyMediumVisualOwnership()
	{
		foreach(var pair in _mediumEntries)
		{
			bool sectorVisible=_mediumVolumetricVisualDesired.Contains(pair.Key)&&IsMediumSiblingGroupReady(pair.Key);
			for(int i=0;i<8;i++)
				pair.Value.Presenters[i].Visible=sectorVisible&&!IsFineChildReadyVisualOwner(MediumChildCoordinate(pair.Key,i));
		}
	}
	private bool IsMediumSiblingGroupReady(SmoothTerrainChunkCoordinate c)
	{
		if(!IsStitchedCaveLodEnabled)return true;
		var p=new SmoothTerrainChunkCoordinate(FloorDiv(c.AroundChunk,4)*4,RingCoordMath.FloorDiv(c.VerticalChunk,4)*4,RingCoordMath.FloorDiv(c.AcrossChunk,4)*4);
		return MediumChildrenReady(p);
	}
	private void ScheduleMedium(SmoothTerrainChunkCoordinate coordinate,SmoothTerrainVolumetricDensitySnapshot field)
	{
		if(_stopping)return;
		var cancel=CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
		ulong generation=_generation;
		SmoothTerrainPresenter[] presenters=Enumerable.Range(0,8).Select(_=>AcquireMediumPresenter()).ToArray(); foreach(SmoothTerrainPresenter presenter in presenters)presenter.Visible=false;
		RingLodGeometrySnapshot geometry=_geometry;Transform3D ownerInverse=GlobalTransform.AffineInverse();
		bool stitchTransitions=IsStitchedCaveLodEnabled;
		byte transitionMask=CaptureMediumTransitionMask(coordinate);var entry=new MediumEntry(generation,field.Identity,cancel,presenters){TransitionMask=transitionMask};
		_mediumEntries.Add(coordinate,entry);
		Task work;DeferMeshWork(MeshWorkTier.Medium,entry.Token,()=>{work=Task.Run(async()=>
		{
			bool acquired=false;
			try
			{
				CancellationToken token=entry.Token;await _workers.WaitAsync(token);acquired=true;token.ThrowIfCancellationRequested();
				using SmoothTerrainVolumetricWorkerContext workerContext=field.CreateWorkerContext(UseIndependentWorkerSamplers);
				SmoothTerrainVolumetricDensitySnapshot workerField=workerContext.Field;
				MediumCapture capture=CaptureMedium(coordinate,workerField);
				// Uniform central regions cannot contain a regular-cell
				// isosurface.  Do not allocate the 37^3 snapshot or invoke
				// the mesher merely because its gradient halo changed sign.
				SmoothTerrainMeshData[] local=capture.Range.Classification==SmoothTerrainDensityClassification.Mixed
					? BuildMediumChildMeshes(capture.Snapshot!)
					: Enumerable.Repeat(EmptyMediumMesh,8).ToArray();
				if(stitchTransitions&&transitionMask!=0)
					AddMediumTransitionMeshes(local,BuildBridgeTransitions(workerField,coordinate,2,transitionMask,capture.Snapshot));
				SmoothTerrainMeshData[] meshes=local.Select(mesh=>TransformWorker(mesh,coordinate,geometry,ownerInverse,workerContext.Sampler??_sourceSampler)).ToArray();
				token.ThrowIfCancellationRequested();_mediumReady.Enqueue(new MediumResult(coordinate,generation,field.Identity,meshes,capture.Range));
			}
			catch(OperationCanceledException) { }
			finally {if(acquired)_workers.Release();entry.CompleteWorkAndDisposeToken();}
		},CancellationToken.None);TrackMeshWork(work);},entry.CompleteWorkAndDisposeToken);
	}
	private byte CaptureMediumTransitionMask(SmoothTerrainChunkCoordinate c){byte m=0;if(!_mediumVolumetricVisualDesired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk-2,c.VerticalChunk,c.AcrossChunk)))m|=1;if(!_mediumVolumetricVisualDesired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk+2,c.VerticalChunk,c.AcrossChunk)))m|=2;if(!_mediumVolumetricVisualDesired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk,c.VerticalChunk-2,c.AcrossChunk)))m|=4;if(!_mediumVolumetricVisualDesired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk,c.VerticalChunk+2,c.AcrossChunk)))m|=8;if(!_mediumVolumetricVisualDesired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk,c.VerticalChunk,c.AcrossChunk-2)))m|=16;if(!_mediumVolumetricVisualDesired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk,c.VerticalChunk,c.AcrossChunk+2)))m|=32;return m;}
	private static readonly SmoothTerrainMeshData EmptyMediumMesh=new(){Vertices=Array.Empty<Vector3>(),Normals=Array.Empty<Vector3>(),Materials=Array.Empty<int>(),Indices=Array.Empty<int>(),CollisionFaces=Array.Empty<Vector3>()};
	private readonly record struct MediumCapture(SmoothTerrainDensityRange Range,SmoothTerrainChunkSnapshot? Snapshot);
	private static MediumCapture CaptureMedium(SmoothTerrainChunkCoordinate coordinate,SmoothTerrainVolumetricDensitySnapshot field)
	{
		const int spacing=2,cells=32,halo=2;
		var key=new SmoothTerrainVolumetricDensityCacheKey(field.Identity,coordinate.AroundChunk*32-halo*spacing,coordinate.VerticalChunk*32-halo*spacing,coordinate.AcrossChunk*32-halo*spacing,cells+halo*2,spacing);
		SmoothTerrainRegularChunkCapture capture=field.CaptureRegularChunk(key,coordinate);
		return new MediumCapture(capture.Range,capture.Snapshot);
	}
	private static SmoothTerrainMeshData ScaleMesh(SmoothTerrainMeshData mesh,float factor)
	{
		var vertices=new Vector3[mesh.Vertices.Length];for(int i=0;i<vertices.Length;i++)vertices[i]=mesh.Vertices[i]*factor;
		return new SmoothTerrainMeshData{Vertices=vertices,Normals=mesh.Normals,Materials=mesh.Materials,Indices=mesh.Indices,CollisionFaces=Array.Empty<Vector3>(),BiomeTextureWeights=mesh.BiomeTextureWeights};
	}
	// The medium capture is one 32-cell (64 m) lattice.  These portions use
	// its shared samples at the 16-cell planes, so they are an exact partition
	// of the regular marching-cell work rather than eight density captures.
	private static SmoothTerrainMeshData[] BuildMediumChildMeshes(SmoothTerrainChunkSnapshot snapshot)
	{
		var pieces=new SmoothTerrainMeshData[8];
		for(int i=0;i<8;i++)
		{
			int x=(i&1)*16,y=((i>>1)&1)*16,z=((i>>2)&1)*16;
			pieces[i]=ScaleMesh(SmoothTerrainMesher.Build(snapshot,x,y,z,x+16,y+16,z+16,false),2.0f);
		}
		return pieces;
	}
	// Native Transvoxel faces are emitted in metre coordinates.  They only
	// touch an outer 64 m face, but partition them anyway so a child never
	// inherits another child's exterior seam geometry.
	private static void AddMediumTransitionMeshes(SmoothTerrainMeshData[] children,SmoothTerrainMeshData transition)
	{
		SmoothTerrainMeshData[] parts=PartitionMediumMesh(transition);
		for(int i=0;i<8;i++)children[i]=CombineRenderMeshes(children[i],parts[i]);
	}
	private static SmoothTerrainMeshData[] PartitionMediumMesh(SmoothTerrainMeshData mesh)
	{
		var vertices=new List<Vector3>[8];var normals=new List<Vector3>[8];var materials=new List<int>[8];var weights=mesh.BiomeTextureWeights is null?null:new List<Color>[8];var indices=new List<int>[8];var remap=new Dictionary<int,int>[8];
		for(int i=0;i<8;i++){vertices[i]=new();normals[i]=new();materials[i]=new();indices[i]=new();remap[i]=new();if(weights!=null)weights[i]=new();}
		for(int q=0;q+2<mesh.Indices.Length;q+=3)
		{
			int a=mesh.Indices[q],b=mesh.Indices[q+1],c=mesh.Indices[q+2];Vector3 centroid=(mesh.Vertices[a]+mesh.Vertices[b]+mesh.Vertices[c])/3f;
			int x=Math.Clamp((int)MathF.Floor(centroid.X/32f),0,1),y=Math.Clamp((int)MathF.Floor(centroid.Y/32f),0,1),z=Math.Clamp((int)MathF.Floor(centroid.Z/32f),0,1),piece=x+2*(y+2*z);
			foreach(int old in new[]{a,b,c}){if(!remap[piece].TryGetValue(old,out int next)){next=vertices[piece].Count;remap[piece].Add(old,next);vertices[piece].Add(mesh.Vertices[old]);normals[piece].Add(mesh.Normals[old]);materials[piece].Add(mesh.Materials[old]);if(weights!=null)weights[piece].Add(mesh.BiomeTextureWeights![old]);}indices[piece].Add(next);}
		}
		var result=new SmoothTerrainMeshData[8];for(int i=0;i<8;i++)result[i]=new SmoothTerrainMeshData{Vertices=vertices[i].ToArray(),Normals=normals[i].ToArray(),Materials=materials[i].ToArray(),Indices=indices[i].ToArray(),CollisionFaces=Array.Empty<Vector3>(),BiomeTextureWeights=weights?[i].ToArray()};return result;
	}
	private int InstallMediumReady(int installLimit)
	{
		if(SuspendInstallsForTests)return 0;
		int installed=0;
		while(((installed<installLimit&&FrameInstallBudgetAvailable())||(_mediumReady.TryPeek(out MediumResult peeked)&&IsBookkeepingSector(peeked.Meshes)))&&_mediumReady.TryDequeue(out MediumResult result))
		{
			if(!_mediumEntries.TryGetValue(result.Coordinate,out MediumEntry? entry)||entry.Generation!=result.Generation||entry.Identity!=result.Identity)continue;
			entry.Classification=result.Range.Classification;
			entry.UniformRejected=result.Range.Classification!=SmoothTerrainDensityClassification.Mixed;
			entry.Ready=true;
			// This tier is deliberately incapable of collision. Near remains
			// the sole physical cave representation until a later LOD step.
			for(int child=0;child<8;child++){entry.Presenters[child].Position=MediumSectorWorldOrigin(result.Coordinate);entry.Presenters[child].Install(result.Meshes[child],false,_terrainMaterial);}
			if(!IsBookkeepingSector(result.Meshes))installed++;
		}
		ApplyMediumVisualOwnership();
		ApplyBridgeOwnership();
		return installed;
	}
	private Vector3 MediumSectorWorldOrigin(SmoothTerrainChunkCoordinate coordinate)=>ChunkWorldOrigin(coordinate);
	private SmoothTerrainPresenter AcquireMediumPresenter()
	{
		if(_mediumPool.Count>0)return _mediumPool.Dequeue();
		var p=new SmoothTerrainPresenter();AddChild(p);return p;
	}
	private void RemoveMediumEntry(SmoothTerrainChunkCoordinate coordinate,MediumEntry entry)
	{
		if(entry.RequestCancel())Interlocked.Increment(ref _auxiliaryCancelRequestCount);foreach(SmoothTerrainPresenter presenter in entry.Presenters){presenter.Reset();_mediumPool.Enqueue(presenter);}_mediumEntries.Remove(coordinate);
	}
	private void ClearMediumVolumetricPresentation()
	{
		foreach(var pair in _mediumEntries.ToArray())RemoveMediumEntry(pair.Key,pair.Value);
		_mediumVolumetricVisualDesired.Clear();
	}
	private void RefreshBridgeVolumetricDesired(SmoothTerrainVolumetricDensitySnapshot field)
	{
		if(!IsStitchedCaveLodEnabled||!IsCoarseUndergroundFocus()){ClearBridgeVolumetricPresentation();return;}
		for(int i=_bridgeTiers.Length-1;i>=0;i--)RefreshBridgeTier(_bridgeTiers[i],field);
		ApplyBridgeOwnership();
	}
	private void RefreshBridgeTier(BridgeTier tier,SmoothTerrainVolumetricDensitySnapshot field)
	{
		int meters=tier.Extent, scale=meters/32, parentMeters=meters*2; long around=RingCoordMath.WrapAround((long)Math.Floor(_focus.AroundMeters),_baseline!.AroundLength);int across=Math.Clamp((int)Math.Floor(_focus.AcrossMeters),0,_baseline.AcrossWidth);int vertical=(int)Math.Floor(_focus.VerticalMeters);
		// Two aligned parent sectors per axis, expanded to all four fine
		// children. This is deliberately fixed, not radius-shaped: a parent
		// can only disappear after its complete child set is ready.
		long ParentStart(long v){long p=FloorDiv(v,parentMeters);return v-p*parentMeters<parentMeters/2?p-1:p;}
		long pa=ParentStart(around),pc=ParentStart(across),pv=ParentStart(vertical);var wanted=new HashSet<SmoothTerrainChunkCoordinate>();
		// Around origins stay locally unwrapped. The field and geometry wrap
		// samples themselves; wrapping a ceil-count creates an overlapping
		// partial sector at the ring seam and breaks transition alignment.
		for(int z=0;z<4;z++)for(int x=0;x<4;x++)for(int y=0;y<4;y++){long childA=pa*2+x;int childC=(int)(pc*2+z),childV=(int)(pv*2+y),oa=childC*meters,ov=childV*meters;if(oa>_baseline.AcrossWidth||oa+meters<0||ov+meters<StructuralBottomVertical)continue;var c=new SmoothTerrainChunkCoordinate(childA*scale,childV*scale,childC*scale);wanted.Add(c);if(childC==0)wanted.Add(new SmoothTerrainChunkCoordinate(childA*scale,childV*scale,-scale));}
		tier.Desired.Clear();tier.Desired.UnionWith(wanted);
		foreach(var p in tier.Entries.ToArray())if(!wanted.Contains(p.Key))RemoveBridgeEntry(tier,p.Key,p.Value);
		foreach(var c in wanted)
			if(!tier.Entries.TryGetValue(c,out BridgeEntry? e))ScheduleBridge(tier,c,field);
			else if(e.Identity!=field.Identity)
			{
				if(HasOverlappingEditSince(e.CheckedThroughEditRevision,c,tier.Spacing))
				{ RemoveBridgeEntry(tier,c,e); ScheduleBridge(tier,c,field); }
				else e.CheckedThroughEditRevision=_editRevision;
			}
	}
	private void ApplyBridgeOwnership()
	{
		// Atomic handoff: a parent remains visible until every one of its
		// aligned 2x2x2 child sectors is installed and visible-capable.
		for(int i=0;i<_bridgeTiers.Length;i++) foreach(var p in _bridgeTiers[i].Entries)
			p.Value.Presenter.Visible=_bridgeTiers[i].Desired.Contains(p.Key)&&IsBridgeSiblingGroupReady(_bridgeTiers[i],p.Key)&&!IsBridgeCoveredByFiner(i,p.Key);
		foreach(var p in _coarseEntries)p.Value.Presenter.Visible=_coarseVolumetricVisualDesired.Contains(p.Key)&&!IsCoarseCoveredByBridge(p.Key);
	}
	private bool IsBridgeCoveredByFiner(int tierIndex,SmoothTerrainChunkCoordinate parent)
	{
		if(tierIndex==0) return MediumChildrenReady(parent);
		return ChildrenReady(_bridgeTiers[tierIndex-1],parent);
	}
	private bool IsCoarseCoveredByBridge(SmoothTerrainChunkCoordinate parent)=>ChildrenReady(_bridgeTiers[^1],parent);
	private bool MediumChildrenReady(SmoothTerrainChunkCoordinate parent)
	{for(int z=0;z<2;z++)for(int y=0;y<2;y++)for(int x=0;x<2;x++){var c=new SmoothTerrainChunkCoordinate(parent.AroundChunk+x*2,parent.VerticalChunk+y*2,parent.AcrossChunk+z*2);if(!_mediumVolumetricVisualDesired.Contains(c)||!_mediumEntries.TryGetValue(c,out MediumEntry? e)||(!e.Presenters.Any(p=>p.HasRenderMesh)&&!e.UniformRejected))return false;}return true;}
	private static bool ChildrenReady(BridgeTier child,SmoothTerrainChunkCoordinate parent)
	{
		int childScale=child.Extent/32;for(int z=0;z<2;z++)for(int y=0;y<2;y++)for(int x=0;x<2;x++){var c=new SmoothTerrainChunkCoordinate(parent.AroundChunk+x*childScale,parent.VerticalChunk+y*childScale,parent.AcrossChunk+z*childScale);if(!child.Desired.Contains(c)||!child.Entries.TryGetValue(c,out BridgeEntry? e)||!e.Ready)return false;}return true;
	}
	private static bool IsBridgeSiblingGroupReady(BridgeTier tier,SmoothTerrainChunkCoordinate c){int s=tier.Extent/32;var p=new SmoothTerrainChunkCoordinate(FloorDiv(c.AroundChunk,s*2)*(s*2),RingCoordMath.FloorDiv(c.VerticalChunk,s*2)*(s*2),RingCoordMath.FloorDiv(c.AcrossChunk,s*2)*(s*2));return ChildrenReady(tier,p);}
	private void ScheduleBridge(BridgeTier tier,SmoothTerrainChunkCoordinate c,SmoothTerrainVolumetricDensitySnapshot field)
	{
		if(_stopping)return;
		var cancel=CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
		var presenter=AcquireBridgePresenter(tier);presenter.Visible=false;
		byte mask=CaptureTransitionMask(tier,c);
		var entry=new BridgeEntry(_generation,field.Identity,cancel,presenter){TransitionMask=mask};
		tier.Entries.Add(c,entry);
		RingLodGeometrySnapshot geometry=_geometry;Transform3D inverse=GlobalTransform.AffineInverse();
		Task work;DeferMeshWork(MeshWorkTier.Bridge,entry.Token,()=>{work=Task.Run(async()=>
		{
			bool acquired=false;
			try
			{
				await _workers.WaitAsync(entry.Token);acquired=true;entry.Token.ThrowIfCancellationRequested();
				using SmoothTerrainVolumetricWorkerContext workerContext=field.CreateWorkerContext(UseIndependentWorkerSamplers);
				SmoothTerrainVolumetricDensitySnapshot workerField=workerContext.Field;
				MediumCapture capture=CaptureVolumetric(c,workerField,tier.Spacing);
				SmoothTerrainMeshData local=capture.Range.Classification==SmoothTerrainDensityClassification.Mixed
					?ScaleMesh(SmoothTerrainMesher.Build(capture.Snapshot!,false),tier.Spacing)
					:EmptyMediumMesh;
				int transitionTriangles=0;
				if(mask!=0)
				{
					SmoothTerrainMeshData transition=BuildBridgeTransitions(workerField,c,tier.Spacing,mask,capture.Snapshot);
					transitionTriangles=transition.Indices.Length/3;
					local=CombineRenderMeshes(local,transition);
				}
					SmoothTerrainMeshData mesh=TransformWorker(local,c,geometry,inverse,workerContext.Sampler??_sourceSampler);
				entry.Token.ThrowIfCancellationRequested();
				tier.Ready.Enqueue(new BridgeResult(c,entry.Generation,field.Identity,mesh,capture.Range,transitionTriangles));
			}
			catch(OperationCanceledException){}
			finally{if(acquired)_workers.Release();entry.CompleteWorkAndDisposeToken();}
		},CancellationToken.None);TrackMeshWork(work);},entry.CompleteWorkAndDisposeToken);
	}
	private static SmoothTerrainMeshData BuildBridgeTransitions(SmoothTerrainVolumetricDensitySnapshot field,SmoothTerrainChunkCoordinate c,int spacing,byte mask,SmoothTerrainChunkSnapshot? capturedFineLattice=null)
	{ if(mask==0)return EmptyMediumMesh;var o=new RingTerrainSampleCoordinate(c.AroundChunk*32,c.VerticalChunk*32,c.AcrossChunk*32);int parent=spacing*64;var co=new RingTerrainSampleCoordinate(FloorDiv(o.AroundVoxel,parent)*parent,(int)(RingCoordMath.FloorDiv(o.VerticalVoxel,parent)*parent),(int)(RingCoordMath.FloorDiv(o.AcrossVoxel,parent)*parent));return SmoothTerrainTransitionMesher.BuildFaces(field,o,spacing,co,mask,capturedFineLattice); }
	// All current cave call sites combine exactly two meshes.  Keeping that
	// contract avoids a short-lived array and LINQ enumerators per bridge
	// sector without changing the mesh ownership of either input.
	private static SmoothTerrainMeshData CombineRenderMeshes(SmoothTerrainMeshData first,SmoothTerrainMeshData second)
	{
		int vc=checked(first.Vertices.Length+second.Vertices.Length),ic=checked(first.Indices.Length+second.Indices.Length);
		var v=new Vector3[vc];var n=new Vector3[vc];var mat=new int[vc];var idx=new int[ic];
		int vo=0,io=0;
		Copy(first);Copy(second);
		return new SmoothTerrainMeshData{Vertices=v,Normals=n,Materials=mat,Indices=idx,CollisionFaces=Array.Empty<Vector3>()};
		void Copy(SmoothTerrainMeshData mesh)
		{
			Array.Copy(mesh.Vertices,0,v,vo,mesh.Vertices.Length);
			Array.Copy(mesh.Normals,0,n,vo,mesh.Normals.Length);
			Array.Copy(mesh.Materials,0,mat,vo,mesh.Materials.Length);
			for(int i=0;i<mesh.Indices.Length;i++)idx[io+i]=mesh.Indices[i]+vo;
			vo+=mesh.Vertices.Length;io+=mesh.Indices.Length;
		}
	}
	private int InstallBridgeReady(int limit){if(SuspendInstallsForTests)return 0;int installed=0;for(int i=_bridgeTiers.Length-1;i>=0;i--){BridgeTier tier=_bridgeTiers[i];while(((installed<limit&&FrameInstallBudgetAvailable())||(tier.Ready.TryPeek(out BridgeResult peeked)&&IsBookkeepingMesh(peeked.Mesh)))&&tier.Ready.TryDequeue(out BridgeResult r)){if(!tier.Entries.TryGetValue(r.Coordinate,out BridgeEntry? e)||e.Generation!=r.Generation||e.Identity!=r.Identity)continue;e.Classification=r.Range.Classification;e.UniformRejected=r.Range.Classification!=SmoothTerrainDensityClassification.Mixed;e.Ready=true;e.TransitionMask=CaptureTransitionMask(tier,r.Coordinate);e.TransitionTriangleCount=r.TransitionTriangleCount;e.Presenter.Position=ChunkWorldOrigin(r.Coordinate);e.Presenter.Install(r.Mesh,false,_terrainMaterial);if(!IsBookkeepingMesh(r.Mesh))installed++;}}ApplyBridgeOwnership();return installed;}
	// Bit order: -/+ around, -/+ vertical, -/+ across. Exposed faces receive
	// real 2:1 transition cells into the next coarser tier.
	private byte CaptureTransitionMask(BridgeTier tier,SmoothTerrainChunkCoordinate c)
	{int s=tier.Extent/32;byte m=0;if(!tier.Desired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk-s,c.VerticalChunk,c.AcrossChunk)))m|=1;if(!tier.Desired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk+s,c.VerticalChunk,c.AcrossChunk)))m|=2;if(!tier.Desired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk,c.VerticalChunk-s,c.AcrossChunk)))m|=4;if(!tier.Desired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk,c.VerticalChunk+s,c.AcrossChunk)))m|=8;if(!tier.Desired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk,c.VerticalChunk,c.AcrossChunk-s)))m|=16;if(!tier.Desired.Contains(new SmoothTerrainChunkCoordinate(c.AroundChunk,c.VerticalChunk,c.AcrossChunk+s)))m|=32;return m;}
	private SmoothTerrainPresenter AcquireBridgePresenter(BridgeTier t){if(t.Pool.Count>0)return t.Pool.Dequeue();var p=new SmoothTerrainPresenter();AddChild(p);return p;}
	private void RemoveBridgeEntry(BridgeTier t,SmoothTerrainChunkCoordinate c,BridgeEntry e){if(e.RequestCancel())Interlocked.Increment(ref _auxiliaryCancelRequestCount);e.Presenter.Reset();t.Pool.Enqueue(e.Presenter);t.Entries.Remove(c);}
	private void ClearBridgeVolumetricPresentation(){if(_bridgeTiers.Any(t=>t.Entries.Count>0))CaveLodPresentationClearCountForTests++;foreach(var t in _bridgeTiers){foreach(var p in t.Entries.ToArray())RemoveBridgeEntry(t,p.Key,p.Value);t.Desired.Clear();}}
	private void RefreshCoarseVolumetricDesired(SmoothTerrainVolumetricDensitySnapshot field)
	{
		if(!EnableCoarseVolumetricVisualCoverage||!IsCoarseUndergroundFocus()){ClearCoarseVolumetricPresentation();return;}
		int ra=Math.Max(0,EffectiveCoarseVisualRadiusAround),rc=Math.Max(0,EffectiveCoarseVisualRadiusAcross),rv=Math.Max(0,EffectiveCoarseVisualRadiusVertical);
		HashSet<SmoothTerrainChunkCoordinate> wanted=BuildCoarseWanted(ra,rc,rv);
		int margin=Math.Max(0,CoarseRetainMarginSectors);
		HashSet<SmoothTerrainChunkCoordinate>? retain=margin>0?BuildCoarseWanted(ra+margin,rc+margin,rv+margin):null;
		_coarseVolumetricVisualDesired.Clear();_coarseVolumetricVisualDesired.UnionWith(wanted);
		foreach(var p in _coarseEntries.ToArray())
		{
			if(wanted.Contains(p.Key))continue;
			CoarseEntry e=p.Value;
			// A ready sector just outside the shrunken ring stays resident but hidden (ApplyBridgeOwnership), so
			// re-growing the ring costs nothing. It is never rescheduled; a stale one is dropped on the next edit overlap.
			if(retain!=null&&e.Ready&&retain.Contains(p.Key))
			{
				if(e.Identity!=field.Identity)
				{
					if(HasOverlappingEditSince(e.CheckedThroughEditRevision,p.Key,64))RemoveCoarseEntry(p.Key,e);
					else e.CheckedThroughEditRevision=_editRevision;
				}
				continue;
			}
			RemoveCoarseEntry(p.Key,e);
		}
		foreach(var c in wanted)
			if(!_coarseEntries.TryGetValue(c,out CoarseEntry? e))ScheduleCoarse(c,field);
			else if(e.Identity!=field.Identity&&HasOverlappingEditSince(e.CheckedThroughEditRevision,c,64))
			{
				RemoveCoarseEntry(c,e);
				ScheduleCoarse(c,field);
			}
			else if(e.Identity!=field.Identity)e.CheckedThroughEditRevision=_editRevision;
		ApplyBridgeOwnership();
	}
	private HashSet<SmoothTerrainChunkCoordinate> BuildCoarseWanted(int radiusAround,int radiusAcross,int radiusVertical)
	{
		const int meters=2048, parentScale=64;
		long around=RingCoordMath.WrapAround((long)Math.Floor(_focus.AroundMeters),_baseline!.AroundLength);
		int across=Math.Clamp((int)Math.Floor(_focus.AcrossMeters),0,_baseline.AcrossWidth);
		long ca=FloorDiv(around,meters); int cc=RingCoordMath.FloorDiv(across,meters),cv=RingCoordMath.FloorDiv((int)Math.Floor(_focus.VerticalMeters),meters);
		bool stitchedClipmap=IsStitchedCaveLodEnabled;
		long count=Math.Max(1,(_baseline.AroundLength+meters-1)/meters);var wanted=new HashSet<SmoothTerrainChunkCoordinate>();
		for(int z=-radiusAcross;z<=radiusAcross;z++)for(long x=-radiusAround;x<=radiusAround;x++)for(int y=-radiusVertical-1;y<=radiusVertical;y++)
		{ long sa=stitchedClipmap?ca+x:RingCoordMath.WrapAroundChunk(ca+x,count);int sc=cc+z,sv=cv+y,oa=sc*meters,ov=sv*meters;
		  if(oa>_baseline.AcrossWidth||oa+meters<0||ov+meters<StructuralBottomVertical)continue;
		  var c=new SmoothTerrainChunkCoordinate(sa*parentScale,sv*parentScale,sc*parentScale);wanted.Add(c);if(sc==0)wanted.Add(new SmoothTerrainChunkCoordinate(sa*parentScale,sv*parentScale,-parentScale)); }
		return wanted;
	}
	private bool _coarseUndergroundLatched;
	/// <summary>Enter at the activation depth, release only after rising CoarseActivationHysteresisMeters above it. Returns true when the latch flipped.</summary>
	private bool UpdateCoarseUndergroundLatch(in RingCanonicalPosition focus)
	{
		bool next=false;
		if(_sourceSampler!=null&&_baseline!=null)
		{
			long a=RingCoordMath.WrapAround((long)Math.Floor(focus.AroundMeters),_baseline.AroundLength);int c=Math.Clamp((int)Math.Floor(focus.AcrossMeters),0,_baseline.AcrossWidth);
			double depth=_sourceSampler.GetSurfaceHeight(a,c)-focus.VerticalMeters;
			float enter=Math.Max(0,CoarseUndergroundActivationDepthMeters),exit=Math.Max(0,enter-Math.Max(0,CoarseActivationHysteresisMeters));
			next=_coarseUndergroundLatched?depth>=exit:depth>=enter;
		}
		if(next==_coarseUndergroundLatched)return false;
		_coarseUndergroundLatched=next;CoarseUndergroundFocusTransitionCountForTests++;return true;
	}
	private bool IsCoarseUndergroundFocus()=>_coarseUndergroundLatched;
	private void ScheduleCoarse(SmoothTerrainChunkCoordinate c,SmoothTerrainVolumetricDensitySnapshot field)
	{ if(_stopping)return;CoarseScheduledCountForTests++;var cancel=CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);var p=AcquireCoarsePresenter();p.Visible=false;var e=new CoarseEntry(_generation,field.Identity,cancel,p);_coarseEntries.Add(c,e);RingLodGeometrySnapshot geo=_geometry;Transform3D inv=GlobalTransform.AffineInverse();Task work;DeferMeshWork(MeshWorkTier.Coarse,e.Token,()=>{work=Task.Run(async()=>{bool acquired=false;try{await _workers.WaitAsync(e.Token);acquired=true;e.Token.ThrowIfCancellationRequested();using SmoothTerrainVolumetricWorkerContext workerContext=field.CreateWorkerContext(UseIndependentWorkerSamplers);MediumCapture cap=CaptureCoarse(c,workerContext.Field);SmoothTerrainMeshData local=cap.Range.Classification==SmoothTerrainDensityClassification.Mixed?ScaleMesh(SmoothTerrainMesher.Build(cap.Snapshot!,false),64):EmptyMediumMesh;SmoothTerrainMeshData mesh=TransformWorker(local,c,geo,inv,workerContext.Sampler??_sourceSampler);e.Token.ThrowIfCancellationRequested();_coarseReady.Enqueue(new CoarseResult(c,e.Generation,field.Identity,mesh,cap.Range));}catch(OperationCanceledException){}finally{if(acquired)_workers.Release();e.CompleteWorkAndDisposeToken();}},CancellationToken.None);TrackMeshWork(work);},e.CompleteWorkAndDisposeToken); }
	private static MediumCapture CaptureCoarse(SmoothTerrainChunkCoordinate c,SmoothTerrainVolumetricDensitySnapshot field)
		=> CaptureVolumetric(c,field,64);
	private bool StampOverlapsVolumetricCapture(SmoothTerrainChunkCoordinate c,in SmoothTerrainEditCommand edit,int spacing)
	{
		if(_baseline==null)return true;
		return StampOverlapsCapture(c,edit,_baseline.AroundLength,spacing);
	}
	private bool HasOverlappingEditSince(ulong entryRevision,SmoothTerrainChunkCoordinate coordinate,int spacing)
	{
		if(entryRevision>=_editRevision)return false;
		for(ulong revision=entryRevision+1;revision<=_editRevision;revision++)
		{
			// A restored/non-incremental journal has no revision history. The
			// conservative path is required in that rare case.
			if(!_editRevisionCommands.TryGetValue(revision,out SmoothTerrainEditCommand edit))return true;
			if(StampOverlapsVolumetricCapture(coordinate,edit,spacing))return true;
			if(revision==_editRevision)break;
		}
		return false;
	}
	private void PruneEditRevisionHistory()
	{
		// History is only needed while a resident auxiliary entry still has an
		// older field identity. Base parents carry their own immutable target
		// snapshots and never consult this table. This bounds a long editing
		// session to the revision lag of the visible cave LODs; at the surface
		// it is immediately empty.
		bool hasAuxiliary=false;
		ulong oldest=_editRevision;
		void Consider(ulong revision)
		{
			hasAuxiliary=true;
			if(revision<oldest)oldest=revision;
		}
		foreach(NearTransitionEntry entry in _nearTransitionEntries.Values)Consider(entry.CheckedThroughEditRevision);
		foreach(MediumEntry entry in _mediumEntries.Values)Consider(entry.CheckedThroughEditRevision);
		foreach(BridgeTier tier in _bridgeTiers)foreach(BridgeEntry entry in tier.Entries.Values)Consider(entry.CheckedThroughEditRevision);
		foreach(CoarseEntry entry in _coarseEntries.Values)Consider(entry.CheckedThroughEditRevision);
		if(!hasAuxiliary){_editRevisionCommands.Clear();return;}
		foreach(ulong revision in _editRevisionCommands.Keys.Where(revision=>revision<=oldest).ToArray())_editRevisionCommands.Remove(revision);
	}
	private static MediumCapture CaptureVolumetric(SmoothTerrainChunkCoordinate c,SmoothTerrainVolumetricDensitySnapshot field,int spacing)
	{ const int cells=32,halo=2;var key=new SmoothTerrainVolumetricDensityCacheKey(field.Identity,c.AroundChunk*32-halo*spacing,c.VerticalChunk*32-halo*spacing,c.AcrossChunk*32-halo*spacing,cells+halo*2,spacing);SmoothTerrainRegularChunkCapture capture=field.CaptureRegularChunk(key,c);return new MediumCapture(capture.Range,capture.Snapshot); }
	private int InstallCoarseReady(int limit){if(SuspendInstallsForTests)return 0;int i=0;while(((i<limit&&FrameInstallBudgetAvailable())||(_coarseReady.TryPeek(out CoarseResult peeked)&&IsBookkeepingMesh(peeked.Mesh)))&&_coarseReady.TryDequeue(out CoarseResult r)){if(!_coarseEntries.TryGetValue(r.Coordinate,out CoarseEntry? e)||e.Generation!=r.Generation||e.Identity!=r.Identity)continue;e.Classification=r.Range.Classification;e.UniformRejected=r.Range.Classification!=SmoothTerrainDensityClassification.Mixed;e.Ready=true;e.Presenter.Position=ChunkWorldOrigin(r.Coordinate);e.Presenter.Install(r.Mesh,false,_terrainMaterial);if(!IsBookkeepingMesh(r.Mesh))i++;}ApplyBridgeOwnership();return i;}
	private SmoothTerrainPresenter AcquireCoarsePresenter(){if(_coarsePool.Count>0)return _coarsePool.Dequeue();var p=new SmoothTerrainPresenter();AddChild(p);return p;}
	private void RemoveCoarseEntry(SmoothTerrainChunkCoordinate c,CoarseEntry e){if(e.RequestCancel())Interlocked.Increment(ref _auxiliaryCancelRequestCount);e.Presenter.Reset();_coarsePool.Enqueue(e.Presenter);_coarseEntries.Remove(c);}
	private void ClearCoarseVolumetricPresentation(){if(_coarseEntries.Count>0)CaveLodPresentationClearCountForTests++;foreach(var p in _coarseEntries.ToArray())RemoveCoarseEntry(p.Key,p.Value);_coarseVolumetricVisualDesired.Clear();}
	private void UpdateSurfaceResidency(in RingCanonicalPosition focus)
	{
		if(!EnableAltitudeAwareResidency||_sourceSampler==null)
		{
			_surfaceResidencyActive=true;
			return;
		}
		long around=RingCoordMath.WrapAround((long)Math.Floor(focus.AroundMeters),_baseline!.AroundLength);
		int across=Math.Clamp((int)Math.Floor(focus.AcrossMeters),0,_baseline.AcrossWidth);
		double altitude=focus.VerticalMeters-_sourceSampler.GetSurfaceHeight(around,across);
		float enter=Math.Max(1.0f,SurfaceResidencyEnterAltitudeMeters);
		float exit=Math.Max(enter,SurfaceResidencyExitAltitudeMeters);
		_surfaceResidencyActive=_surfaceResidencyActive?altitude<=exit:altitude<=enter;
	}
	private void SuspendSurfacePresentation()
	{
		_lastHotPatchAnchor=null;
		_visualDesired.Clear();
		_surfaceVisualDesired.Clear();
		_nearVolumetricVisualDesired.Clear();
		ClearNearTransitionPresentation();
		ClearMediumVolumetricPresentation();
		ClearBridgeVolumetricPresentation();
		ClearCoarseVolumetricPresentation();
		_collisionDesired.Clear();
		_editSubdivisionDesired.Clear();
		_residentBuildDesired.Clear();
		foreach(var pair in new List<KeyValuePair<SmoothTerrainChunkCoordinate,Entry>>(_entries))RemoveEntry(pair.Key,pair.Value);
		_urgentRebuilds.Clear();
		_urgentSnapshots.Clear();
		_urgentPatchMasks.Clear();
		// Commands and edit snapshots are authoritative journal state; only
		// disposable presenters and in-flight residency work are removed.
		RecountReady();
		UpdateCurrentMetrics(0);
		BumpVisualCoverageRevision();
	}
	private void Schedule(SmoothTerrainChunkCoordinate c,ulong generation,SmoothTerrainEditSnapshot edits,SmoothTerrainVolumetricDensitySnapshot field,SmoothTerrainChunkSnapshot? preparedSnapshot=null,byte patchMask=0xff)
	{
		if(_baseline==null)return;
		bool hot=IsHotPatchCoordinate(c);
		SmoothTerrainPresenter? retainedPresenter=null;
		SmoothTerrainPresenter?[]? retainedPatches=null;
		SmoothTerrainMeshData?[]? retainedPatchData=null;
		bool retainedReady=false,retainedCollision=false;
		if(_entries.TryGetValue(c,out Entry? old))
		{
			if(old.RequestCancel()){Interlocked.Increment(ref _cancelRequestCount);_diagnostics.GetOrAdd(c,_=>new DiagnosticRecord()).Cancelled();}
			// Retaining the old, proven mesh is important for both visual
			// continuity and physics.  The incoming result installs into this
			// same presenter on the main thread, so replacement is atomic.
			retainedPresenter=old.Presenter;
			retainedPatches=old.Patches;
			retainedPatchData=old.PatchData;
			retainedReady=old.Ready;
			retainedCollision=old.Collision;
		}
		RingLodGeometrySnapshot geometry=_geometry; Transform3D ownerInverse=GlobalTransform.AffineInverse();
		Entry entry; Task work;
		lock(_lifetimeGate)
		{
			if(_stopping)return;
			entry=new Entry(generation,new SmoothTerrainRevision(_baseline.Revision,edits.Revision),CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token))
			{ Presenter=retainedPresenter, Patches=retainedPatches, PatchData=retainedPatchData, Ready=retainedReady, Collision=retainedCollision, TargetSnapshot=preparedSnapshot, HotPatches=hot, LastPatchMask=patchMask };
			_entries[c]=entry;
			_diagnostics.GetOrAdd(c,_=>new DiagnosticRecord()).Scheduled();
			CancellationToken token=entry.Token;
			MeshWorkTier workTier=_urgentRebuilds.Contains(c)||_collisionDesired.Contains(c)
				?MeshWorkTier.CriticalBase:MeshWorkTier.Base;
			DeferMeshWork(workTier, token, () => { work=Task.Run(async()=> { bool acquired=false;try { await _workers.WaitAsync(token);acquired=true;entry.MarkWorkStarted();int active=Interlocked.Increment(ref _activeWorkers);int prior;do{prior=_maxActiveWorkers;if(active<=prior)break;}while(Interlocked.CompareExchange(ref _maxActiveWorkers,active,prior)!=prior);Task? gate;lock(_testGateLock)gate=_testWorkerGate?.Task;if(gate!=null)await gate.WaitAsync(token);using var diagnosticScope=RingPerformanceTelemetry.MeasureCpu("Smooth terrain build", worker:true);var timer=Stopwatch.StartNew(); token.ThrowIfCancellationRequested(); using SmoothTerrainVolumetricWorkerContext workerContext=field.CreateWorkerContext(UseIndependentWorkerSamplers); SmoothTerrainChunkSnapshot snapshot=preparedSnapshot??workerContext.Field.CaptureChunk(c);entry.TargetSnapshot=snapshot; // Incremental edit snapshots stay on the optimized hot path; ordinary streaming uses the authoritative field.
				SmoothTerrainMeshData mesh=new SmoothTerrainMeshData{Vertices=Array.Empty<Vector3>(),Normals=Array.Empty<Vector3>(),Materials=Array.Empty<int>(),Indices=Array.Empty<int>(),CollisionFaces=Array.Empty<Vector3>()};
				SmoothTerrainMeshData?[]? patches=null;
				if(hot)
				{
					patches=new SmoothTerrainMeshData?[8];
					for(int patch=0;patch<8;patch++)
					{
						if((patchMask&(1<<patch))==0)continue;
						int x=(patch&1)*16,y=((patch>>1)&1)*16,z=((patch>>2)&1)*16;
						patches[patch]=TransformWorker(SmoothTerrainMesher.Build(snapshot,x,y,z,x+16,y+16,z+16,false),c,geometry,ownerInverse,workerContext.Sampler??_sourceSampler);
					}
				}
				else if(retainedPatchData is { Length:8 } && retainedPatchData.All(patch=>patch!=null))
				{
					mesh=MergePatchMeshes(retainedPatchData);
					Interlocked.Increment(ref _hotPatchMergeCount);
				}
				else mesh=TransformWorker(SmoothTerrainMesher.Build(snapshot,false),c,geometry,ownerInverse,workerContext.Sampler??_sourceSampler);
				token.ThrowIfCancellationRequested(); timer.Stop(); ulong hash=SmoothTerrainMeshHash.Compute(mesh);
				if(patches!=null)foreach(SmoothTerrainMeshData? patch in patches)if(patch!=null)unchecked{hash=(hash^SmoothTerrainMeshHash.Compute(patch))*1099511628211UL;}
				_diagnostics.GetOrAdd(c,_=>new DiagnosticRecord()).Completed(); _ready.Enqueue(new Result(c,generation,snapshot.Revision,snapshot,mesh,patches,hash,timer.Elapsed.TotalMilliseconds)); } catch(OperationCanceledException) { } finally {if(acquired){Interlocked.Decrement(ref _activeWorkers);_workers.Release();} entry.CompleteWorkAndDisposeToken();} },CancellationToken.None); TrackMeshWork(work); }, entry.CompleteWorkAndDisposeToken);
		}
	}
	private int InstallReady(int requestedInstallLimit)
	{
		using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Smooth terrain install");
		if(SuspendInstallsForTests){RecountReady();UpdateCurrentMetrics();return 0;}
		if(_ready.IsEmpty)return 0;
		var timer=Stopwatch.StartNew();int installed=0,installLimit=requestedInstallLimit;bool coverageChanged=false,forceBatchCommit=false;while((((installed<installLimit)&&(forceBatchCommit||FrameInstallBudgetAvailable()))||(_ready.TryPeek(out Result peeked)&&peeked.Patches==null&&IsBookkeepingMesh(peeked.Mesh)))&&_ready.TryDequeue(out Result r))
		{ if(!_entries.TryGetValue(r.Coordinate,out Entry? e)||e.Generation!=r.Generation||e.ExpectedRevision!=r.Revision){_diagnostics.GetOrAdd(r.Coordinate,_=>new DiagnosticRecord()).Stale();Metrics=Metrics with{StaleResults=Metrics.StaleResults+1};continue;}
			if(_editBatch?.Revision==r.Revision.Edits&&_editBatch.Expected.Contains(r.Coordinate))
			{
				_editBatch.Staged[r.Coordinate]=r;
				if(_editBatch.Staged.Count<_editBatch.Expected.Count)continue;
				// Drain this exact revision in one main-thread transaction.
				// Every old collider stays active until all replacements have
				// been staged, then collision installation below precedes the
				// deferred retirement path for each parent.
				Result[] batch=_editBatch.Staged.Values.ToArray();_editBatch=null;_batchRetained.Clear();_refreshAfterBatchCommit=true;
				foreach(Result staged in batch)_ready.Enqueue(staged);
				forceBatchCommit=true;installLimit=Math.Max(installLimit,installed+batch.Length);continue;
			}
			bool collision=WithinCollision(r.Coordinate);
			if(e.HotPatches && r.Patches!=null)
			{
				// Promotion is a parent-level transaction. All eight collision
				// shapes are installed before the retained 32^3 collider is
				// retired, so the physics world never observes an uncovered
				// frame while the worker results are consumed.
				e.Patches ??= new SmoothTerrainPresenter?[8];
				e.PatchData ??= new SmoothTerrainMeshData?[8];
				for(int patch=0;patch<8;patch++)
				{
					if(r.Patches[patch]==null)continue;
					SmoothTerrainPresenter p=e.Patches[patch]??=AcquirePresenter();
					p.Position=ChunkWorldOrigin(r.Coordinate); p.Visible=_visualDesired.Contains(r.Coordinate);
					p.Install(r.Patches[patch]!,collision,_terrainMaterial);
					e.PatchData[patch]=r.Patches[patch];
				}
				if(e.Presenter!=null){ReleasePresenterAfterPhysics(e.Presenter);e.Presenter=null;}
				e.Collision=collision && e.Patches.Any(p=>p?.HasCollision==true);
			}
			else
			{
				SmoothTerrainPresenter p=e.Presenter??=AcquirePresenter();
				p.Visible=_visualDesired.Contains(r.Coordinate);p.Install(r.Mesh,collision,_terrainMaterial);p.Position=ChunkWorldOrigin(r.Coordinate);
				// Demotion uses the inverse handoff: the new merged collider is
				// live before any patch collider is released.
				if(e.Patches!=null){foreach(SmoothTerrainPresenter? patch in e.Patches)if(patch!=null)ReleasePresenterAfterPhysics(patch);e.Patches=null;}
				e.PatchData=null;
				e.Collision=p.HasCollision;
			}
			e.TargetSnapshot=r.Snapshot;e.Ready=true;e.Installs++;coverageChanged=true;_diagnostics.GetOrAdd(r.Coordinate,_=>new DiagnosticRecord()).Installed(r.Revision,r.Hash,r.Generation,EntryHasRender(e),e.Collision);Metrics=Metrics with{LastWorkerMilliseconds=r.WorkerMilliseconds,MaxWorkerMilliseconds=Math.Max(Metrics.MaxWorkerMilliseconds,r.WorkerMilliseconds),Installed=Metrics.Installed+1};if(r.Patches!=null||!IsBookkeepingMesh(r.Mesh))installed++; }
		if(coverageChanged){BumpVisualCoverageRevision();ApplyVisualOwnership();ApplyNearTransitionOwnership();ApplyMediumVisualOwnership();ApplyBridgeOwnership();}
		if(_refreshAfterBatchCommit){_refreshAfterBatchCommit=false;RefreshDesired();}
		RecountReady(); UpdateCurrentMetrics(); Metrics=Metrics with{LastInstallMilliseconds=timer.Elapsed.TotalMilliseconds,MaxInstallMilliseconds=Math.Max(Metrics.MaxInstallMilliseconds,timer.Elapsed.TotalMilliseconds)};return installed;
	}
	private bool StampOverlaps(SmoothTerrainChunkCoordinate c,SmoothTerrainEditCommand e)
	{
		return _baseline!=null&&StampOverlapsCapture(c,e,_baseline.AroundLength,1);
	}
	private byte PatchMaskForStamp(SmoothTerrainChunkCoordinate c,SmoothTerrainEditCommand e)
	{
		return _baseline==null?unchecked((byte)0xff):PatchMaskForStamp(c,e,_baseline.AroundLength);
	}
	private static bool StampOverlapsCapture(SmoothTerrainChunkCoordinate c,in SmoothTerrainEditCommand edit,long aroundLength,int spacing)
	{
		const int cells=32,halo=2;
		RingTerrainSampleCoordinate origin=c.Origin;
		long around=NearestPeriodicImage(edit.Around,origin.AroundVoxel,aroundLength);
		return SquaredDistanceToBox(around,edit.Vertical,edit.Across,
			origin.AroundVoxel-halo*spacing,origin.AroundVoxel+(cells+halo)*spacing,
			origin.VerticalVoxel-halo*spacing,origin.VerticalVoxel+(cells+halo)*spacing,
			origin.AcrossVoxel-halo*spacing,origin.AcrossVoxel+(cells+halo)*spacing)<= (double)edit.Radius*edit.Radius;
	}
	private static byte PatchMaskForStamp(SmoothTerrainChunkCoordinate c,in SmoothTerrainEditCommand e,long aroundLength)
	{
		byte mask=0;RingTerrainSampleCoordinate origin=c.Origin;
		long around=NearestPeriodicImage(e.Around,origin.AroundVoxel,aroundLength);
		const int halo=SmoothTerrainChunkSnapshot.Halo;
		for(int patch=0;patch<8;patch++)
		{
			int px=(patch&1)*16,py=((patch>>1)&1)*16,pz=((patch>>2)&1)*16;
			if(SquaredDistanceToBox(around,e.Vertical,e.Across,
				origin.AroundVoxel+px-halo,origin.AroundVoxel+px+16+halo,
				origin.VerticalVoxel+py-halo,origin.VerticalVoxel+py+16+halo,
				origin.AcrossVoxel+pz-halo,origin.AcrossVoxel+pz+16+halo)<= (double)e.Radius*e.Radius)mask|=(byte)(1<<patch);
		}
		return mask==0?unchecked((byte)0xff):mask;
	}
	private static long NearestPeriodicImage(long around,long originAround,long aroundLength)
	{
		long center=RingCoordMath.WrapAround(around,aroundLength);
		return center+aroundLength*(long)Math.Round((originAround-center)/(double)aroundLength);
	}
	private static double SquaredDistanceToBox(long around,int vertical,int across,long minimumAround,long maximumAround,int minimumVertical,int maximumVertical,int minimumAcross,int maximumAcross)
	{
		double dx=(double)around-Math.Clamp((double)around,minimumAround,maximumAround);
		double dy=vertical-Math.Clamp(vertical,minimumVertical,maximumVertical);
		double dz=across-Math.Clamp(across,minimumAcross,maximumAcross);
		return dx*dx+dy*dy+dz*dz;
	}
	internal static bool CertifiedRingStampPrecisionRegressionTest(out string summary)
	{
		long aroundLength=CertifiedRingScale.AroundVoxelLength;
		long oppositeAround=aroundLength/2;
		if(!CheckOneMeterBoundaries(new SmoothTerrainChunkCoordinate(oppositeAround/32,0,0),aroundLength)
			|| !CheckOneMeterBoundaries(new SmoothTerrainChunkCoordinate(937_500,0,0),aroundLength))
		{ summary="certified one-metre stamp boundary was not classified precisely"; return false; }
		summary="15,707,968m opposite-side and 30,000,000m float-precision one-metre capture and hot-patch boundaries passed";
		return true;
		static bool CheckOneMeterBoundaries(SmoothTerrainChunkCoordinate chunk,long circumference)
		{
			RingTerrainSampleCoordinate origin=chunk.Origin;
			var patchBoundary=new SmoothTerrainEditCommand(1,1,1,false,origin.AroundVoxel+35,25,25,1,(int)SmoothTerrainMaterial.Rock);
			var volumetricBoundary=new SmoothTerrainEditCommand(1,2,2,false,origin.AroundVoxel+(34*64)+1,25,25,1,(int)SmoothTerrainMaterial.Rock);
			return StampOverlapsCapture(chunk,patchBoundary,circumference,1)
				&& PatchMaskForStamp(chunk,patchBoundary,circumference)==0x80
				&& StampOverlapsCapture(chunk,volumetricBoundary,circumference,64);
		}
	}
	private void RemoveEntry(SmoothTerrainChunkCoordinate coordinate,Entry entry)
	{
		if(entry.RequestCancel())
		{
			Interlocked.Increment(ref _cancelRequestCount);
			_diagnostics.GetOrAdd(coordinate,_=>new DiagnosticRecord()).Cancelled();
		}
		if(entry.Presenter!=null)ReleasePresenter(entry.Presenter);
		if(entry.Patches!=null)foreach(SmoothTerrainPresenter? patch in entry.Patches)if(patch!=null)ReleasePresenter(patch);
		_entries.Remove(coordinate);
		BumpVisualCoverageRevision();
		_diagnostics.GetOrAdd(coordinate,_=>new DiagnosticRecord()).Current(false,false,false,false);
	}
	// Pending is the number of desired entries without an installed result. A queued result
	// remains represented by its non-ready entry, so it is intentionally not double-counted.
	private void UpdateCurrentMetrics(int? desiredOverride=null)
	{
		int pending=0; long bytes=0;
		foreach(var pair in _entries)
		{
			Entry e=pair.Value; bool render=EntryHasRender(e), collision=EntryHasCollision(e);
			if(!e.Ready) pending++;
			if(e.Presenter!=null) bytes+=e.Presenter.RetainedByteSize;
			if(e.Patches!=null)foreach(SmoothTerrainPresenter? patch in e.Patches)if(patch!=null)bytes+=patch.RetainedByteSize;
			_diagnostics.GetOrAdd(pair.Key,_=>new DiagnosticRecord()).Current(true,e.Ready,render,collision);
		}
		Metrics=Metrics with { Desired=Math.Max(0,desiredOverride??Metrics.Desired), VisualDesired=Math.Max(0,_visualDesired.Count), CollisionDesired=Math.Max(0,_collisionDesired.Count), EditSubdivisionDesired=Math.Max(0,_editSubdivisionDesired.Count), QueueDepth=Math.Max(0,_tasks.Count+DeferredWorkCount), Pending=Math.Max(0,pending), ActiveWorkers=Math.Max(0,Volatile.Read(ref _activeWorkers)), MaxActiveWorkers=Math.Max(0,Volatile.Read(ref _maxActiveWorkers)), Resident=Math.Max(0,_entries.Count), Ready=Math.Max(0,ReadyRenderCount), Pool=Math.Max(0,_pool.Count), Cancelled=Math.Max(0,Volatile.Read(ref _cancelRequestCount)), EstimatedBytes=Math.Max(0,bytes) };
	}
	private void BumpVisualCoverageRevision(){unchecked{_visualCoverageRevision++;if(_visualCoverageRevision==0)_visualCoverageRevision=1;}}
	private static long FloorDiv(long value,long divisor){long quotient=value/divisor,remainder=value%divisor;return remainder<0?quotient-1:quotient;}
	private bool IsStitchedCaveLodEnabled=>EnableFocusCenteredVisualCoverage
		&&EnableMediumVolumetricVisualCoverage
		&&EnableVolumetricBridgeShells
		&&EnableCoarseVolumetricVisualCoverage
		&&SmoothTerrainTransitionMesher.IsNativeAvailable
		&&IsCoarseUndergroundFocus();
	private long Shortest(long a,long b,long length){long d=a-b;d%=length;if(d>length/2)d-=length;if(d<-length/2)d+=length;return d;}
	private bool WithinCollision(SmoothTerrainChunkCoordinate c)
	{
		return _collisionDesired.Contains(c);
	}
	private bool IsHotPatchCoordinate(SmoothTerrainChunkCoordinate c)
	{
		if(!EnableHotChunkPatches||_baseline==null)return false;
		if((long)c.VerticalChunk*SmoothTerrainChunkSnapshot.Cells<StructuralBottomVertical)return false;
		SmoothTerrainChunkCoordinate anchor=GetHotPatchAnchor();long x=anchor.AroundChunk;int y=anchor.VerticalChunk,z=anchor.AcrossChunk;
		long count=Math.Max(1,(_baseline.AroundLength+31)/32);
		long dx=Shortest(c.AroundChunk,x,count);
		return dx>=0&&dx<=1&&c.VerticalChunk>=y&&c.VerticalChunk<=y+1&&c.AcrossChunk>=z&&c.AcrossChunk<=z+1;
	}
	private SmoothTerrainChunkCoordinate GetHotPatchAnchor()
	{
		if(_baseline==null)return default;
		long around=RingCoordMath.WrapAround((long)Math.Floor(_focus.AroundMeters),_baseline.AroundLength);
		int across=Math.Clamp((int)Math.Floor(_focus.AcrossMeters),0,_baseline.AcrossWidth);
		int vertical=(int)Math.Floor(_focus.VerticalMeters); long count=Math.Max(1,(_baseline.AroundLength+31)/32);
		if(_lastHotPatchAnchor is SmoothTerrainChunkCoordinate previous)
		{
			float h=Mathf.Max(0,HotChunkHysteresisMeters);long previousAround=previous.AroundChunk*32;
			double dx=Shortest(around,previousAround,_baseline.AroundLength);
			// A two-parent span is centred on the focus. Its normal anchor
			// transition is at each 32 m parent midpoint (16 and 48 relative
			// to the old anchor), not at the outer edges of the 64 m span.
			// Hysteresis widens those transition planes without ever allowing
			// the focus to leave the hot bubble.
			if(dx>=16-h&&dx<48+h
				&&vertical>=previous.VerticalChunk*32+16-h&&vertical<previous.VerticalChunk*32+48+h
				&&across>=previous.AcrossChunk*32+16-h&&across<previous.AcrossChunk*32+48+h)return previous;
		}
		// Around has already been canonicalised and is non-negative.  Vertical
		// is not: C# integer division truncates toward zero, so use floor
		// division here or a player at Y=-55 anchors to the wrong 32 m slab.
		long x=FloorDiv(around,32);int y=RingCoordMath.FloorDiv(vertical,32),z=RingCoordMath.FloorDiv(across,32);
		if(around%32<16)x--;if(((vertical%32)+32)%32<16)y--;if(across%32<16)z--;
		return new SmoothTerrainChunkCoordinate(RingCoordMath.WrapAroundChunk(x,count),y,z);
	}
	private IEnumerable<SmoothTerrainChunkCoordinate> EnumerateHotPatchCoordinates()
	{
		if(!EnableHotChunkPatches||_baseline==null)yield break;
		SmoothTerrainChunkCoordinate anchor=GetHotPatchAnchor();long x=anchor.AroundChunk;int y=anchor.VerticalChunk,z=anchor.AcrossChunk;
		long count=Math.Max(1,(_baseline.AroundLength+31)/32);
		for(int dz=0;dz<2;dz++)for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)
		{
			int candidateAcross=z+dz;
			int candidateVertical=y+dy;
			if(candidateAcross*32>_baseline.AcrossWidth||(candidateAcross+1)*32<0)continue;
			// A parent beginning below the structural bottom is entirely
			// outside the finite SDF. The parent beginning at the bottom is
			// retained so it owns the underside collider.
			if((long)candidateVertical*SmoothTerrainChunkSnapshot.Cells<StructuralBottomVertical)continue;
			yield return new SmoothTerrainChunkCoordinate(RingCoordMath.WrapAroundChunk(x+dx,count),candidateVertical,candidateAcross);
		}
	}
	private void ServiceHotPatchDemotions(double delta)
	{
		if(!EnableHotChunkPatches||_baseline==null||_entries.Count==0)return;
		_hotPatchElapsed+=delta;
		if(_hotPatchElapsed<.1)return;
		double elapsed=_hotPatchElapsed;_hotPatchElapsed=0;
		var edits=EnsureEditSnapshot()??throw new InvalidOperationException("Smooth terrain edit snapshot is unavailable.");
		foreach(var pair in _entries.ToArray())
		{
			Entry entry=pair.Value;
			if(!entry.HotPatches||IsHotPatchCoordinate(pair.Key)){entry.OutsideHotSeconds=0;continue;}
			entry.OutsideHotSeconds+=elapsed;
			if(entry.OutsideHotSeconds<Math.Max(.25,HotChunkDemotionIdleSeconds))continue;
			// Keep the eight presenters alive while the merged replacement is
			// generated; Schedule carries them into the new entry.
			SmoothTerrainVolumetricDensitySnapshot? field=CaptureVolumetricDensitySnapshot();
			if(field==null)break;
			_generation++;Schedule(pair.Key,_generation,edits,field);break;
		}
	}
	private static bool EntryHasRender(Entry entry)
		=> entry.Presenter?.HasRenderMesh==true || entry.Patches?.Any(p=>p?.HasRenderMesh==true)==true;
	private static bool EntryHasCollision(Entry entry)
		=> entry.Presenter?.HasCollision==true || entry.Patches?.Any(p=>p?.HasCollision==true)==true;
	private static SmoothTerrainMeshData MergePatchMeshes(IReadOnlyList<SmoothTerrainMeshData?> patches)
	{
		int vertexCount=0,indexCount=0,faceCount=0;
		bool preserveBiomeWeights=true;
		foreach(SmoothTerrainMeshData? patch in patches)if(patch!=null)
		{
			vertexCount+=patch.Vertices.Length;indexCount+=patch.Indices.Length;faceCount+=patch.CollisionFaces.Length;
			preserveBiomeWeights&=patch.BiomeTextureWeights?.Length==patch.Vertices.Length;
		}
		var vertices=new Vector3[vertexCount];var normals=new Vector3[vertexCount];var materials=new int[vertexCount];
		var indices=new int[indexCount];var faces=new Vector3[faceCount];
		Color[]? biomeWeights=preserveBiomeWeights?new Color[vertexCount]:null;
		int vertexOffset=0,indexOffset=0,faceOffset=0;
		foreach(SmoothTerrainMeshData? patch in patches)if(patch!=null)
		{
			Array.Copy(patch.Vertices,0,vertices,vertexOffset,patch.Vertices.Length);
			Array.Copy(patch.Normals,0,normals,vertexOffset,patch.Normals.Length);
			Array.Copy(patch.Materials,0,materials,vertexOffset,patch.Materials.Length);
			if(biomeWeights!=null)Array.Copy(patch.BiomeTextureWeights!,0,biomeWeights,vertexOffset,patch.Vertices.Length);
			for(int i=0;i<patch.Indices.Length;i++)indices[indexOffset+i]=patch.Indices[i]+vertexOffset;
			Array.Copy(patch.CollisionFaces,0,faces,faceOffset,patch.CollisionFaces.Length);
			vertexOffset+=patch.Vertices.Length;indexOffset+=patch.Indices.Length;faceOffset+=patch.CollisionFaces.Length;
		}
		return new SmoothTerrainMeshData{Vertices=vertices,Normals=normals,Materials=materials,Indices=indices,CollisionFaces=faces,BiomeTextureWeights=biomeWeights};
	}
	private long ChunkDistanceToFocus(SmoothTerrainChunkCoordinate c)
	{
		if(_baseline==null)return long.MaxValue;
		SmoothTerrainChunkCoordinate center=_lastFocusCenter??default;
		long count=Math.Max(1,(_baseline.AroundLength+SmoothTerrainChunkSnapshot.Cells-1)/SmoothTerrainChunkSnapshot.Cells);
		return Math.Abs(Shortest(c.AroundChunk,center.AroundChunk,count))
			+Math.Abs(c.AcrossChunk-center.AcrossChunk)
			+Math.Abs(c.VerticalChunk-center.VerticalChunk);
	}
	private int GetSurfaceVerticalChunk(long around,int across)
	{
		if(_baseline==null||_sourceSampler==null)return 0;
		long canonicalAround=RingCoordMath.WrapAround(around,_baseline.AroundLength);
		int canonicalAcross=Math.Clamp(across,0,_baseline.AcrossWidth);
		RingLodSurfaceEditSnapshot edits=CaptureLodSurfaceEditSnapshot();
		double surface=edits.TryGetEditedSurfaceHeight(canonicalAround,canonicalAcross,out int edited)
			? edited+.5
			: _baseline.GetSmoothSurfaceHeight(canonicalAround,canonicalAcross)+.5;
		return (int)Math.Floor(surface/SmoothTerrainChunkSnapshot.Cells);
	}
	private void GetColumnSurfaceChunkRange(long aroundChunk,int acrossChunk,out int minimum,out int maximum)
	{
		if(_baseline==null){minimum=maximum=0;return;}
		minimum=int.MaxValue;
		maximum=int.MinValue;
		for(int z=0;z<=2;z++)
		for(int x=0;x<=2;x++)
		{
			long around=aroundChunk*SmoothTerrainChunkSnapshot.Cells+x*(SmoothTerrainChunkSnapshot.Cells/2);
			int across=Math.Clamp(
				acrossChunk*SmoothTerrainChunkSnapshot.Cells+z*(SmoothTerrainChunkSnapshot.Cells/2),
				0,
				_baseline.AcrossWidth);
			int vertical=GetSurfaceVerticalChunk(around,across);
			minimum=Math.Min(minimum,vertical);
			maximum=Math.Max(maximum,vertical);
		}
		if(minimum==int.MaxValue)minimum=maximum=0;
	}
	private bool ApplyVisualOwnership()
	{
		bool changed=false;
		foreach (var pair in _entries)
		{
			// A near-only cave child is presented atomically per containing
			// 64 m medium sector. This prevents a partial near install from
			// duplicating the still-visible medium surface. Surface owners
			// retain their established immediate presentation behaviour.
			bool stitchedNear=IsStitchedCaveLodEnabled;
			bool visible = _surfaceVisualDesired.Contains(pair.Key)
				|| (_nearVolumetricVisualDesired.Contains(pair.Key) && (!stitchedNear||IsNearSiblingGroupReady(pair.Key)));
			if (pair.Value.Presenter != null && pair.Value.Presenter.Visible != visible)
			{
				pair.Value.Presenter.Visible = visible;
				changed=true;
			}
			if (pair.Value.Patches != null)
				foreach (SmoothTerrainPresenter? patch in pair.Value.Patches)
					if (patch != null && patch.Visible != visible)
					{
						patch.Visible = visible;
						changed=true;
					}
		}
		return changed;
	}
	private bool IsNearSiblingGroupReady(SmoothTerrainChunkCoordinate child)
	{
		var parent=new SmoothTerrainChunkCoordinate(FloorDiv(child.AroundChunk,2)*2,RingCoordMath.FloorDiv(child.VerticalChunk,2)*2,RingCoordMath.FloorDiv(child.AcrossChunk,2)*2);
		for(int z=0;z<2;z++)for(int y=0;y<2;y++)for(int x=0;x<2;x++)
		{ var sibling=new SmoothTerrainChunkCoordinate(parent.AroundChunk+x,parent.VerticalChunk+y,parent.AcrossChunk+z);
		  if(!_nearVolumetricVisualDesired.Contains(sibling)||!_entries.TryGetValue(sibling,out Entry? entry)||!entry.Ready)return false; }
		return true;
	}
	private void ApplyCollisionDesired()
	{
		foreach(var pair in _entries)
		{
			bool want=_collisionDesired.Contains(pair.Key);
			Entry entry=pair.Value;
			if(!entry.Ready||entry.Collision==want)continue;
			if(entry.Patches!=null){foreach(SmoothTerrainPresenter? patch in entry.Patches)patch?.SetCollision(want);entry.Collision=EntryHasCollision(entry);}
			else if(entry.Presenter!=null){entry.Presenter.SetCollision(want);entry.Collision=entry.Presenter.HasCollision;}
		}
	}
	private void PruneHistoricalDiagnostics()
	{
		const int maximumRecords=1024,targetRecords=768;
		if(_diagnostics.Count<=maximumRecords)return;
		foreach(var pair in _diagnostics)
		{
			if(_diagnostics.Count<=targetRecords)break;
			if(!_entries.ContainsKey(pair.Key))_diagnostics.TryRemove(pair.Key,out _);
		}
	}
	private Vector3 ChunkWorldOrigin(SmoothTerrainChunkCoordinate c)=>GlobalTransform.AffineInverse()*_geometry.CanonicalToWorld(c.AroundChunk*32,c.VerticalChunk*32,c.AcrossChunk*32);
	internal static SmoothTerrainMeshData TransformWorker(SmoothTerrainMeshData mesh,SmoothTerrainChunkCoordinate c,RingLodGeometrySnapshot g,Transform3D inverse,RingTerrainSampler? biomeSampler=null)
	{
		long aroundOrigin=c.AroundChunk*SmoothTerrainChunkSnapshot.Cells;
		int verticalOrigin=c.VerticalChunk*SmoothTerrainChunkSnapshot.Cells;
		int acrossOrigin=c.AcrossChunk*SmoothTerrainChunkSnapshot.Cells;
		Vector3 origin=inverse*g.CanonicalToWorld(aroundOrigin,verticalOrigin,acrossOrigin);
		var vertices=new Vector3[mesh.Vertices.Length];
		var normals=new Vector3[vertices.Length];
		Basis normalTransform=inverse.Basis.Inverse().Transposed();
		for(int i=0;i<vertices.Length;i++)
		{
			Vector3 local=mesh.Vertices[i];
			// Around coordinates exceed float's one-metre precision on the
			// certified ring. Never add a local vertex to a float canonical
			// origin: at the user's 28,779 km site that rounded alternate
			// lattice rows onto each other and opened arch-facing ribbons.
			double around=aroundOrigin+(double)local.X;
			double vertical=verticalOrigin+(double)local.Y;
			double acrossValue=acrossOrigin+(double)local.Z;
			vertices[i]=inverse*g.CanonicalToWorld(around,vertical,acrossValue)-origin;
			Vector3 aroundAxis=(g.CanonicalToWorld(around+1.0,vertical,acrossValue)-g.CanonicalToWorld(around-1.0,vertical,acrossValue)).Normalized();
			Vector3 upAxis=(g.CanonicalToWorld(around,vertical+1.0,acrossValue)-g.CanonicalToWorld(around,vertical-1.0,acrossValue)).Normalized();
			Vector3 acrossAxis=(g.CanonicalToWorld(around,vertical,acrossValue+1.0)-g.CanonicalToWorld(around,vertical,acrossValue-1.0)).Normalized();
			Vector3 sourceNormal=mesh.Normals[i];
			normals[i]=(normalTransform*(aroundAxis*sourceNormal.X+upAxis*sourceNormal.Y+acrossAxis*sourceNormal.Z)).Normalized();
		}
		var indices=(int[])mesh.Indices.Clone();
		for(int i=0;i<indices.Length;i+=3)
		{
			Vector3 face=(vertices[indices[i+1]]-vertices[indices[i]]).Cross(vertices[indices[i+2]]-vertices[indices[i]]);
			if(face.Dot(normals[indices[i]]+normals[indices[i+1]]+normals[indices[i+2]])>0)
				(indices[i+1],indices[i+2])=(indices[i+2],indices[i+1]);
		}
		Vector3[] faces;
		if(mesh.CollisionFaces.Length==0)faces=Array.Empty<Vector3>();
		else
		{
			// Collision faces must own their transformed vertex array, but a
			// direct indexed copy avoids the LINQ iterator/intermediate work
			// in every collision-bearing chunk transform.
			faces=new Vector3[indices.Length];
			for(int i=0;i<indices.Length;i++)faces[i]=vertices[indices[i]];
		}
		Color[]? biomeWeights = null;
		if (biomeSampler != null && mesh.Vertices.Length > 0)
		{
			biomeWeights = new Color[mesh.Vertices.Length];
			RingTerrainSampleCoordinate meshOrigin = c.Origin;
			// Biome fields deliberately vary on kilometre scales. Sampling a
			// compact 16 m grid during a mesh build preserves the visible
			// blend while avoiding one climate/hydrology query per vertex.
			const int biomeSampleSpacing = 16;
			var biomeCache = new Dictionary<(long Around, int Across), Color>();
			for (int i = 0; i < mesh.Vertices.Length; i++)
			{
				Vector3 local = mesh.Vertices[i];
				long around = meshOrigin.AroundVoxel + Mathf.RoundToInt(local.X);
				int across = meshOrigin.AcrossVoxel + Mathf.RoundToInt(local.Z);
				// Structural sidewalls and off-ring transition geometry have
				// zero weights; UV2 still records their material identity so
				// the shader keeps them on its dirt/rock path.
				if (across >= 0 && across < biomeSampler.RingAcrossWidthMeters)
				{
					long sampledAround = (long)Math.Floor(around / (double)biomeSampleSpacing) * biomeSampleSpacing + biomeSampleSpacing / 2;
					int sampledAcross = (int)Math.Floor(across / (double)biomeSampleSpacing) * biomeSampleSpacing + biomeSampleSpacing / 2;
					sampledAcross = Mathf.Clamp(sampledAcross, 0, biomeSampler.RingAcrossWidthMeters - 1);
					var key = (sampledAround, sampledAcross);
					if (!biomeCache.TryGetValue(key, out Color weight))
					{
						weight = biomeSampler.GetBiomeTextureWeights(sampledAround, sampledAcross).ToVertexColor();
						biomeCache.Add(key, weight);
					}
					biomeWeights[i] = weight;
				}
			}
		}
		return new SmoothTerrainMeshData{Vertices=vertices,Normals=normals,Materials=mesh.Materials,Indices=indices,CollisionFaces=faces,BiomeTextureWeights=biomeWeights};
	}
	private SmoothTerrainPresenter AcquirePresenter()
	{
		if(_pool.Count>0){ReusedPresenterCount++;return _pool.Dequeue();}
		return NewPresenter();
	}
	private void ReleasePresenter(SmoothTerrainPresenter presenter){presenter.Reset();_pool.Enqueue(presenter);}
	private void ReleasePresenterAfterPhysics(SmoothTerrainPresenter presenter)=>_physicsDeferredReleases.Enqueue(presenter);
	private SmoothTerrainPresenter NewPresenter(){CreatedPresenterCount++;_ownedPresenters++;var p=new SmoothTerrainPresenter();AddChild(p);return p;}
	private ShaderMaterial? CreateTerrainMaterial()
	{
		if (_sourceGrid == null) return null;
		Shader? shader=GD.Load<Shader>("res://materials/voxel_ring_terrain.gdshader");
		if(shader==null){GD.PushWarning("Smooth terrain shader could not be loaded; using vertex-color fallback.");return null;}
		var material=new ShaderMaterial{Shader=shader};
		material.SetShaderParameter("pilot_grass_albedo",GD.Load<Texture2D>("res://materials/third_party/voxelgame/grass_albedo.png"));
		material.SetShaderParameter("pilot_dirt_albedo",GD.Load<Texture2D>("res://materials/third_party/voxelgame/dirt_albedo.png"));
		material.SetShaderParameter("biome_desert_albedo",GD.Load<Texture2D>("res://materials/biomes/desert_sand_albedo.png"));
		material.SetShaderParameter("biome_forest_albedo",GD.Load<Texture2D>("res://materials/biomes/forest_floor_albedo.png"));
		material.SetShaderParameter("biome_tundra_albedo",GD.Load<Texture2D>("res://materials/biomes/tundra_ground_albedo.png"));
		material.SetShaderParameter("biome_alpine_albedo",GD.Load<Texture2D>("res://materials/biomes/alpine_rock_albedo.png"));
		material.SetShaderParameter("biome_texture_weights_enabled",true);
		material.SetShaderParameter("pilot_triplanar_enabled",_sourceGrid.UseSmoothTerrainPilotTextures);
		material.SetShaderParameter("pilot_texture_scale",Mathf.Clamp(_sourceGrid.SmoothTerrainPilotTextureScale,.01f,2f));
		RingAtmosphereModel.ConfigureMaterial(material,_sourceGrid.GetCanonicalRingCircumferenceMeters64()/Math.Tau);
		global::RingworldTechDemo.RingCloudShadowProjector.Register(material);
		// A replacement material has no prior uniform state, even if the
		// source values themselves have not changed.
		UpdateTerrainMaterial(material, forceFullUpload: true);
		return material;
	}
	private void UpdateTerrainMaterial()=>UpdateTerrainMaterial(_terrainMaterial, forceFullUpload: false);
	private void UpdateTerrainMaterial(ShaderMaterial? material, bool forceFullUpload)
	{
		if(material==null||_sourceGrid==null)return;
		if(_sunLight==null||!GodotObject.IsInstanceValid(_sunLight))_sunLight=_sourceGrid.GetNodeOrNull<DirectionalLight3D>(_sourceGrid.RingSunLightPath);
		Vector3 ringCenter = _sourceGrid.GetCanonicalRingCenterWorld();
		Vector3 cameraPosition = _streamingFocus?.GlobalPosition ?? Vector3.Zero;
		Vector3 sunDirection = _sunLight?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
		float dayStartDot = _sourceGrid.RingDayStartDot;
		float dayFullDot = Mathf.Max(dayStartDot + .001f, _sourceGrid.RingDayFullDot);
		Color nightTintColor = _sourceGrid.RingNightTint;
		Vector3 nightTint = new(nightTintColor.R, nightTintColor.G, nightTintColor.B);
		float nightDirectLightFactor = _sourceGrid.RingNightDirectLightFactor;
		float dayDirectLightFactor = _sourceGrid.RingDayDirectLightFactor;
		float shadowFloor = _sourceGrid.RingShadowFloor;
		if (forceFullUpload || !_terrainMaterialUniformsValid || ringCenter != _lastTerrainMaterialRingCenter) material.SetShaderParameter("ring_center", ringCenter);
		if (forceFullUpload || !_terrainMaterialUniformsValid || cameraPosition != _lastTerrainMaterialCameraPosition) material.SetShaderParameter("camera_world_position", cameraPosition);
		if (forceFullUpload || !_terrainMaterialUniformsValid || sunDirection != _lastTerrainMaterialSunDirection) material.SetShaderParameter("sun_direction", sunDirection);
		if (forceFullUpload || !_terrainMaterialUniformsValid || dayStartDot != _lastTerrainMaterialDayStartDot) material.SetShaderParameter("day_start_dot", dayStartDot);
		if (forceFullUpload || !_terrainMaterialUniformsValid || dayFullDot != _lastTerrainMaterialDayFullDot) material.SetShaderParameter("day_full_dot", dayFullDot);
		if (forceFullUpload || !_terrainMaterialUniformsValid || nightTint != _lastTerrainMaterialNightTint) material.SetShaderParameter("night_tint", nightTint);
		if (forceFullUpload || !_terrainMaterialUniformsValid || nightDirectLightFactor != _lastTerrainMaterialNightDirectLightFactor) material.SetShaderParameter("night_direct_light_factor", nightDirectLightFactor);
		if (forceFullUpload || !_terrainMaterialUniformsValid || dayDirectLightFactor != _lastTerrainMaterialDayDirectLightFactor) material.SetShaderParameter("day_direct_light_factor", dayDirectLightFactor);
		if (forceFullUpload || !_terrainMaterialUniformsValid || shadowFloor != _lastTerrainMaterialShadowFloor) material.SetShaderParameter("shadow_floor", shadowFloor);
		_lastTerrainMaterialRingCenter = ringCenter;
		_lastTerrainMaterialCameraPosition = cameraPosition;
		_lastTerrainMaterialSunDirection = sunDirection;
		_lastTerrainMaterialDayStartDot = dayStartDot;
		_lastTerrainMaterialDayFullDot = dayFullDot;
		_lastTerrainMaterialNightTint = nightTint;
		_lastTerrainMaterialNightDirectLightFactor = nightDirectLightFactor;
		_lastTerrainMaterialDayDirectLightFactor = dayDirectLightFactor;
		_lastTerrainMaterialShadowFloor = shadowFloor;
		_terrainMaterialUniformsValid = true;
	}
	private void RecountReady(){ReadyRenderCount=0;ReadyCollisionCount=0;foreach(var e in _entries.Values)if(e.Ready){ReadyRenderCount++;if(EntryHasCollision(e))ReadyCollisionCount++;}}
	private sealed class Entry
	{
		private readonly object _tokenGate = new();
		private CancellationTokenSource? _cancel;
		public Entry(ulong generation, SmoothTerrainRevision expectedRevision, CancellationTokenSource cancel) { Generation = generation; ExpectedRevision=expectedRevision; _cancel = cancel; }
		public ulong Generation { get; }
		public SmoothTerrainRevision ExpectedRevision { get; }
		public CancellationToken Token { get { lock (_tokenGate) return _cancel?.Token ?? new CancellationToken(true); } }
		public bool RequestCancel() { lock (_tokenGate) { if(_cancel==null||_cancel.IsCancellationRequested)return false;_cancel.Cancel();return true; } }
		public void CompleteWorkAndDisposeToken() { lock (_tokenGate) { _cancel?.Dispose(); _cancel = null; } }
		private int _workStarted;
		public bool WorkStarted => Volatile.Read(ref _workStarted)!=0;
		public void MarkWorkStarted()=>Interlocked.Exchange(ref _workStarted,1);
		private SmoothTerrainChunkSnapshot? _targetSnapshot;
		public SmoothTerrainChunkSnapshot? TargetSnapshot { get => Volatile.Read(ref _targetSnapshot); set => Volatile.Write(ref _targetSnapshot,value); }
		public SmoothTerrainPresenter? Presenter; public SmoothTerrainPresenter?[]? Patches; public SmoothTerrainMeshData?[]? PatchData; public bool HotPatches; public byte LastPatchMask; public double OutsideHotSeconds; public bool Ready; public bool Collision; public int Builds = 1; public int Installs;
	}
	private sealed class EditBatch
	{
		public EditBatch(ulong revision,IEnumerable<SmoothTerrainChunkCoordinate> expected){Revision=revision;Expected=new HashSet<SmoothTerrainChunkCoordinate>(expected);}
		public ulong Revision { get; }
		public HashSet<SmoothTerrainChunkCoordinate> Expected { get; }
		public Dictionary<SmoothTerrainChunkCoordinate,Result> Staged { get; }=new();
	}
	private sealed class MediumEntry
	{
		private readonly object _cancelGate=new();
		private CancellationTokenSource? _cancel;
		public MediumEntry(ulong generation,SmoothTerrainVolumetricDensityIdentity identity,CancellationTokenSource cancel,SmoothTerrainPresenter[] presenters)
		{Generation=generation;Identity=identity;CheckedThroughEditRevision=identity.EditRevision;_cancel=cancel;Presenters=presenters;}
		public ulong Generation { get; }
		public SmoothTerrainVolumetricDensityIdentity Identity { get; }
		public ulong CheckedThroughEditRevision { get; set; }
		public SmoothTerrainPresenter[] Presenters { get; }
		public SmoothTerrainDensityClassification Classification { get; set; } = SmoothTerrainDensityClassification.Mixed;
		public bool UniformRejected { get; set; } public bool Ready { get; set; } public byte TransitionMask { get; set; }
		public CancellationToken Token { get { lock(_cancelGate)return _cancel?.Token??new CancellationToken(true); } }
		public bool RequestCancel(){lock(_cancelGate){if(_cancel==null||_cancel.IsCancellationRequested)return false;_cancel.Cancel();return true;}}
		public void CompleteWorkAndDisposeToken(){lock(_cancelGate){_cancel?.Dispose();_cancel=null;}}
	}
	private sealed class NearTransitionEntry
	{
		private readonly object _cancelGate=new(); private CancellationTokenSource? _cancel;
		public NearTransitionEntry(ulong generation,SmoothTerrainVolumetricDensityIdentity identity,CancellationTokenSource cancel,SmoothTerrainPresenter presenter,byte faceMask){Generation=generation;Identity=identity;CheckedThroughEditRevision=identity.EditRevision;_cancel=cancel;Presenter=presenter;FaceMask=faceMask;}
		public ulong Generation { get; } public SmoothTerrainVolumetricDensityIdentity Identity { get; } public ulong CheckedThroughEditRevision { get; set; } public SmoothTerrainPresenter Presenter { get; } public byte FaceMask { get; } public bool Ready { get; set; }
		public CancellationToken Token {get{lock(_cancelGate)return _cancel?.Token??new CancellationToken(true);}}
		public bool RequestCancel(){lock(_cancelGate){if(_cancel==null||_cancel.IsCancellationRequested)return false;_cancel.Cancel();return true;}}
		public void CompleteWorkAndDisposeToken(){lock(_cancelGate){_cancel?.Dispose();_cancel=null;}}
	}
	private sealed class CoarseEntry
	{
		private readonly object _cancelGate=new(); private CancellationTokenSource? _cancel;
		public CoarseEntry(ulong generation,SmoothTerrainVolumetricDensityIdentity identity,CancellationTokenSource cancel,SmoothTerrainPresenter presenter){Generation=generation;Identity=identity;CheckedThroughEditRevision=identity.EditRevision;_cancel=cancel;Presenter=presenter;}
		public ulong Generation {get;} public SmoothTerrainVolumetricDensityIdentity Identity {get;} public ulong CheckedThroughEditRevision {get;set;} public SmoothTerrainPresenter Presenter {get;} public SmoothTerrainDensityClassification Classification {get;set;}=SmoothTerrainDensityClassification.Mixed; public bool UniformRejected {get;set;} public bool Ready {get;set;}
		public CancellationToken Token {get{lock(_cancelGate)return _cancel?.Token??new CancellationToken(true);}} public bool RequestCancel(){lock(_cancelGate){if(_cancel==null||_cancel.IsCancellationRequested)return false;_cancel.Cancel();return true;}} public void CompleteWorkAndDisposeToken(){lock(_cancelGate){_cancel?.Dispose();_cancel=null;}}
	}
	private sealed class BridgeTier
	{ public BridgeTier(int spacing){Spacing=spacing;} public int Spacing{get;} public int Extent=>Spacing*32; public HashSet<SmoothTerrainChunkCoordinate> Desired {get;}=new(); public Dictionary<SmoothTerrainChunkCoordinate,BridgeEntry> Entries{get;}=new(); public Queue<SmoothTerrainPresenter> Pool{get;}=new(); public ConcurrentQueue<BridgeResult> Ready{get;}=new(); }
	private sealed class BridgeEntry
	{ private readonly object _cancelGate=new();private CancellationTokenSource? _cancel;public BridgeEntry(ulong generation,SmoothTerrainVolumetricDensityIdentity identity,CancellationTokenSource cancel,SmoothTerrainPresenter presenter){Generation=generation;Identity=identity;CheckedThroughEditRevision=identity.EditRevision;_cancel=cancel;Presenter=presenter;}public ulong Generation{get;}public SmoothTerrainVolumetricDensityIdentity Identity{get;}public ulong CheckedThroughEditRevision{get;set;}public SmoothTerrainPresenter Presenter{get;}public SmoothTerrainDensityClassification Classification{get;set;}=SmoothTerrainDensityClassification.Mixed;public bool UniformRejected{get;set;}public bool Ready{get;set;}public byte TransitionMask{get;set;}public int TransitionTriangleCount{get;set;}public CancellationToken Token{get{lock(_cancelGate)return _cancel?.Token??new CancellationToken(true);}}public bool RequestCancel(){lock(_cancelGate){if(_cancel==null||_cancel.IsCancellationRequested)return false;_cancel.Cancel();return true;}}public void CompleteWorkAndDisposeToken(){lock(_cancelGate){_cancel?.Dispose();_cancel=null;}} }
	private sealed class DiagnosticRecord
	{
		private readonly object _gate=new(); private int _schedule,_completed,_install,_cancel,_stale; private bool _resident,_ready,_render,_collision; private SmoothTerrainRevision _revision; private ulong _hash,_generation;
		public void Scheduled(){lock(_gate)_schedule++;}
		public void Completed(){lock(_gate)_completed++;}
		public void Stale(){lock(_gate)_stale++;}
		public void Cancelled(){lock(_gate)_cancel++;}
		public void Installed(SmoothTerrainRevision revision,ulong hash,ulong generation,bool render,bool collision){lock(_gate){_install++;_revision=revision;_hash=hash;_generation=generation;_resident=true;_ready=true;_render=render;_collision=collision;}}
		public void Current(bool resident,bool ready,bool render,bool collision){lock(_gate){_resident=resident;_ready=ready;_render=render;_collision=collision;}}
		public ChunkDiagnostics Snapshot(){lock(_gate)return new(_schedule,_completed,_install,_cancel,_stale,_resident,_ready,_render,_collision,_revision,_hash,_generation);}
	}
	private readonly record struct Result(SmoothTerrainChunkCoordinate Coordinate,ulong Generation,SmoothTerrainRevision Revision,SmoothTerrainChunkSnapshot Snapshot,SmoothTerrainMeshData Mesh,SmoothTerrainMeshData?[]? Patches,ulong Hash,double WorkerMilliseconds);
	private readonly record struct MediumResult(SmoothTerrainChunkCoordinate Coordinate,ulong Generation,SmoothTerrainVolumetricDensityIdentity Identity,SmoothTerrainMeshData[] Meshes,SmoothTerrainDensityRange Range);
	private readonly record struct NearTransitionResult(SmoothTerrainChunkCoordinate Coordinate,ulong Generation,SmoothTerrainVolumetricDensityIdentity Identity,byte FaceMask,SmoothTerrainMeshData Mesh);
	private readonly record struct CoarseResult(SmoothTerrainChunkCoordinate Coordinate,ulong Generation,SmoothTerrainVolumetricDensityIdentity Identity,SmoothTerrainMeshData Mesh,SmoothTerrainDensityRange Range);
	private readonly record struct BridgeResult(SmoothTerrainChunkCoordinate Coordinate,ulong Generation,SmoothTerrainVolumetricDensityIdentity Identity,SmoothTerrainMeshData Mesh,SmoothTerrainDensityRange Range,int TransitionTriangleCount);
}
public readonly record struct SmoothTerrainRuntimeMetrics(int Desired=0,int QueueDepth=0,int Pending=0,int ActiveWorkers=0,int MaxActiveWorkers=0,int Resident=0,int Ready=0,int Pool=0,int Installed=0,int StaleResults=0,int Cancelled=0,long EstimatedBytes=0,double LastWorkerMilliseconds=0,double MaxWorkerMilliseconds=0,double LastInstallMilliseconds=0,double MaxInstallMilliseconds=0,int VisualDesired=0,int CollisionDesired=0,int EditSubdivisionDesired=0);
public partial class SmoothTerrainPresenter : Node3D
{
	private readonly MeshInstance3D _mesh=new(); private readonly StaticBody3D _body=new(); private readonly CollisionShape3D _shape=new();
	public SmoothTerrainPresenter(){_mesh.CastShadow=GeometryInstance3D.ShadowCastingSetting.On;AddChild(_mesh);AddChild(_body);_body.AddChild(_shape);}
	private Vector3[] _faces=Array.Empty<Vector3>();
	private Vector3[] _vertices=Array.Empty<Vector3>();
	private int[] _indices=Array.Empty<int>();
	private long _retainedByteSize;
	public bool HasRenderMesh => _mesh.Mesh != null;
	public int RenderTriangleCount => _indices.Length/3;
	public bool HasCollision => _shape.Shape != null;
	/// <summary>Exposed for deterministic closure rendering regressions.</summary>
	public GeometryInstance3D.ShadowCastingSetting RenderShadowCasting => _mesh.CastShadow;
	public Material? RenderMaterial => _mesh.MaterialOverride;
	public long RetainedByteSize => _retainedByteSize;
	public int CollisionFaceExpansionCount { get; private set; }
	public string DescribeWorldProbeForTests(Vector3 worldProbe)
	{
		Vector3 local=GlobalTransform.AffineInverse()*worldProbe;
		float nearest=float.PositiveInfinity;
		foreach(Vector3 vertex in _vertices)nearest=Math.Min(nearest,vertex.DistanceTo(local));
		return $"collision={HasCollision},vertices={_vertices.Length},localProbe={local},nearest={nearest:0.000},position={GlobalPosition}";
	}
	private static readonly StandardMaterial3D SharedMaterial=new(){VertexColorUseAsAlbedo=true,Roughness=0.95f};
	public void Install(SmoothTerrainMeshData data,bool collision,Material? terrainMaterial=null){_faces=data.CollisionFaces;_vertices=data.Vertices;_indices=data.Indices;if(data.Indices.Length==0){_mesh.Mesh=null;_shape.Shape=null;_retainedByteSize=0;return;}_retainedByteSize=(long)data.Vertices.Length*12+(long)data.Normals.Length*12+(long)data.Materials.Length*4+(long)data.Indices.Length*4+(long)data.CollisionFaces.Length*12;bool hasBiomeWeights=data.BiomeTextureWeights?.Length==data.Materials.Length;var colors=hasBiomeWeights?(Color[])data.BiomeTextureWeights!.Clone():new Color[data.Materials.Length];if(!hasBiomeWeights&&terrainMaterial==null)for(int i=0;i<colors.Length;i++)colors[i]=data.Materials[i] switch{0=>new Color(0.18f,0.18f,0.20f),1=>new Color(0.34f,0.28f,0.18f),_=>new Color(0.22f,0.40f,0.16f)};var materialIds=new Vector2[data.Materials.Length];for(int i=0;i<materialIds.Length;i++)materialIds[i]=new Vector2(data.Materials[i],0);var arrays=new Godot.Collections.Array();arrays.Resize((int)Mesh.ArrayType.Max);arrays[(int)Mesh.ArrayType.Vertex]=data.Vertices;arrays[(int)Mesh.ArrayType.Normal]=data.Normals;arrays[(int)Mesh.ArrayType.Color]=colors;arrays[(int)Mesh.ArrayType.TexUV2]=materialIds;arrays[(int)Mesh.ArrayType.Index]=data.Indices;var mesh=new ArrayMesh();mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,arrays);_mesh.Mesh=mesh;_mesh.MaterialOverride=terrainMaterial??SharedMaterial;SetCollision(collision);}
	public void SetCollision(bool enabled){if(enabled){if(_faces.Length==0&&_indices.Length>0){_faces=new Vector3[_indices.Length];for(int i=0;i<_indices.Length;i++)_faces[i]=_vertices[_indices[i]];_retainedByteSize+=(long)_faces.Length*12;CollisionFaceExpansionCount++;}if(_faces.Length>0){var shape=new ConcavePolygonShape3D{BackfaceCollision=true};shape.Data=_faces;_shape.Shape=shape;return;}}_shape.Shape=null;}
	public void Reset(){Visible=false;_mesh.Mesh=null;_mesh.MaterialOverride=null;_shape.Shape=null;_faces=Array.Empty<Vector3>();_vertices=Array.Empty<Vector3>();_indices=Array.Empty<int>();_retainedByteSize=0;}
}
