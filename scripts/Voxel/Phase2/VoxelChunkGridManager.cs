using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase1;
using RingworldTechDemo.Voxel.Phase5;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.Phase2;

public partial class VoxelChunkGridManager : Node3D
{
	public const string DefaultDeltaSavePath = "user://voxel_phase2_chunk_deltas.json";
	public const string FloatingOriginBridgeGroup = "voxel_floating_origin_bridge";

	[Export] public bool BuildOnReady { get; set; } = true;
	/// <summary>
	/// Keeps this legacy editable implementation available as a canonical terrain
	/// source for LODs and rollback, without creating chunks or doing frame work.
	/// SmoothTerrainGrid owns editable presentation when this is enabled.
	/// </summary>
	[Export] public bool DormantEditableBackend { get; set; }

	/// <summary>
	/// True when SmoothTerrainGrid owns terrain presentation. The editable grid
	/// remains available as the canonical terrain/noise and edit-data provider,
	/// but must not allocate or render legacy chunk meshes.
	/// </summary>
	public bool IsSmoothTerrainReplacementActive => DormantEditableBackend;
	[Export(PropertyHint.Range, "1,8,1")] public int ChunkRadiusAround { get; set; } = 1;
	[Export(PropertyHint.Range, "1,8,1")] public int ChunkRadiusAcross { get; set; } = 1;
	[Export(PropertyHint.Range, "0,8,1")] public int ChunkRadiusVertical { get; set; } = 0;

	[Export] public bool EnableChunkStreaming { get; set; } = false;
	[Export] public NodePath StreamingFocusPath { get; set; } = new NodePath();
	[Export(PropertyHint.Range, "0.01,1.0,0.01")] public float StreamingUpdateIntervalSeconds { get; set; } = 0.1f;
	[Export(PropertyHint.Range, "0,16,1")] public int StreamingVisualRadiusAround { get; set; } = 2;
	[Export(PropertyHint.Range, "0,16,1")] public int StreamingVisualRadiusAcross { get; set; } = 2;
	[Export(PropertyHint.Range, "0,8,1")] public int StreamingVisualRadiusVertical { get; set; } = 1;
	[Export(PropertyHint.Range, "0,16,1")] public int StreamingCollisionRadiusAround { get; set; } = 1;
	[Export(PropertyHint.Range, "0,16,1")] public int StreamingCollisionRadiusAcross { get; set; } = 1;
	[Export(PropertyHint.Range, "0,8,1")] public int StreamingCollisionRadiusVertical { get; set; } = 0;
	[ExportGroup("Predictive Streaming")]
	[Export] public bool EnablePredictiveStreaming { get; set; } = true;
	[Export(PropertyHint.Range, "0.0,3.0,0.05")] public float PredictionLookAheadSeconds { get; set; } = 0.5f;
	[Export(PropertyHint.Range, "0.0,4096.0,16.0")] public float PredictionMaxDistanceMeters { get; set; } = 512.0f;
	[Export] public bool IncludeBrakingDistanceInPrediction { get; set; } = true;
	[Export(PropertyHint.Range, "1.0,500.0,1.0")] public float PredictionBrakingAcceleration { get; set; } = 45.0f;
	[Export(PropertyHint.Range, "1.0,4.0,0.1")] public float PredictionBrakingSafetyFactor { get; set; } = 1.5f;
	[Export(PropertyHint.Range, "16.0,256.0,16.0")] public float PredictionCorridorStepMeters { get; set; } = 64.0f;
	[Export(PropertyHint.Range, "1,32,1")] public int MaxPredictionCorridorSteps { get; set; } = 8;
	[ExportGroup("Fast Travel Streaming")]
	// Fast movement may increase streaming work, but never invalidates the
	// player's need for current authoritative terrain.
	[Export] public bool SuspendLocalVoxelStreamingDuringFastTravel { get; set; }
	// Retained for scene/save compatibility and deliberately ignored. Movement
	// speed is never allowed to suppress local voxel rendering.
	[Export] public bool HideLocalVoxelMeshesDuringFastTravel { get; set; }
	[Export(PropertyHint.Range, "100,1000000,100")] public float FastTravelSpeedThresholdMetersPerSecond { get; set; } = 750.0f;
	[Export(PropertyHint.Range, "0.0,100.0,0.1")] public float FastTravelStoppedSpeedThresholdMetersPerSecond { get; set; } = 1.0f;
	[Export(PropertyHint.Range, "0.0,2.0,0.05")] public float FastTravelResumeStoppedSeconds { get; set; } = 0.25f;

	[Export(PropertyHint.Range, "1,31,1")] public int InitialSolidHeight { get; set; } = 10;
	[Export] public VoxelBlockColorMap? BlockColorMap { get; set; }
	[Export] public bool UseDeterministicTerrainGeneration { get; set; } = false;
	[Export] public VoxelTerrainSettings? TerrainSettings { get; set; }
	[ExportGroup("Hybrid Terrain Pilot")]
	[Export] public bool UseSmoothNaturalTerrainPilot { get; set; } = false;
	[Export] public bool UseSmoothTerrainPilotTextures { get; set; } = true;
	[Export(PropertyHint.Range, "0.01,2.0,0.01")] public float SmoothTerrainPilotTextureScale { get; set; } = 0.20f;
	[Export] public bool LimitSmoothTerrainShadowsToCollisionRange { get; set; } = true;
	[Export(PropertyHint.Range, "0.5,8.0,0.25")] public float NaturalTerrainSdfBrushRadiusMeters { get; set; } = 1.5f;
	[Export] public bool EnforceExcavationLimits { get; set; } = true;
	[Export(PropertyHint.Range, "-50000,0,1")] public int MinExcavationGlobalY { get; set; } = VoxelConstants.MinVertical;
	[ExportGroup("Deep Vertical Domains")]
	/// <summary>Canonical underside of natural terrain. The cavity starts one metre below this solid boundary.</summary>
	[Export(PropertyHint.Range, "-50000,-32,1")] public int TerrainBottomGlobalY { get; set; } = -8192;
	/// <summary>Thickness of natural terrain reserved against future procedural cave carving.</summary>
	[Export(PropertyHint.Range, "0,8192,1")] public int ProtectedRoofThicknessMeters { get; set; } = 512;
	/// <summary>Solid thickness of the lower structural hull immediately above the structural underside.</summary>
	[Export(PropertyHint.Range, "32,8192,1")] public int LowerHullThicknessMeters { get; set; } = 800;
	[Export] public bool EnforceBedrockLayer { get; set; } = true;
	[Export(PropertyHint.Range, "-50000,8000,1")] public int FlatTerrainBedrockY { get; set; } = 0;
	[Export] public bool EnforceBuildHeightLimit { get; set; } = true;
	[Export(PropertyHint.Range, "0,50000,1")] public int MaxBuildGlobalY { get; set; } = VoxelConstants.MaxVertical;
	[Export] public bool SkipKnownEmptyStreamingChunks { get; set; } = true;
	[Export] public bool SkipKnownSolidStreamingChunks { get; set; } = true;
	[Export(PropertyHint.Range, "32,2048,32")] public int SurfaceTileCacheCapacity { get; set; } = 512;
	[Export] public bool UseDebouncedRebuildQueue { get; set; } = true;
	[Export(PropertyHint.Range, "0.0,0.5,0.005")] public float RebuildDebounceSeconds { get; set; } = 0.04f;
	[Export(PropertyHint.Range, "1,16,1")] public int MaxQueuedRebuildsPerFrame { get; set; } = 2;
	[Export] public bool UseIncrementalChunkLoading { get; set; } = true;
	[Export(PropertyHint.Range, "1,16,1")] public int MaxChunkLoadsPerFrame { get; set; } = 2;
	[Export(PropertyHint.Range, "1,256,1")] public int MaxChunkClassificationsPerFrame { get; set; } = 32;
	[Export(PropertyHint.Range, "0.25,8.0,0.05")] public float MaxChunkStreamingMillisecondsPerFrame { get; set; } = 2.0f;
	[Export(PropertyHint.Range, "0,512,1")] public int MaxPooledChunkNodes { get; set; } = 128;
	[Export] public bool EnableAsyncStreamingPlanning { get; set; } = true;
	[ExportGroup("Adaptive memory residency")]
	[Export] public bool EnableAltitudeAwareStreamingRadii { get; set; } = true;
	[Export(PropertyHint.Range, "32,10000,32")] public float SurfaceResidencyEnterAltitudeMeters { get; set; } = 192.0f;
	[Export(PropertyHint.Range, "32,10000,32")] public float SurfaceResidencyExitAltitudeMeters { get; set; } = 384.0f;
	[Export(PropertyHint.Range, "0,8,1")] public int SurfaceVisualRadiusVertical { get; set; } = 1;
	[Export(PropertyHint.Range, "0,8,1")] public int SurfaceCollisionRadiusVertical { get; set; } = 0;
	[Export] public bool ReleaseUneditedVisualChunkData { get; set; } = true;

	[Export] public bool UseRingCurvedPlacement { get; set; } = false;
	[Export(PropertyHint.Range, "1,20000000,1")] public int AroundRingChunkLength { get; set; } = 256;
	[Export] public bool UseHabitableRingAxisLayout { get; set; } = false;
	[Export] public bool DeriveRingRadiusFromChunkLength { get; set; } = true;
	[Export(PropertyHint.Range, "1.0,10000000.0,0.1")] public float RingRadiusMeters { get; set; } = 1304.0f;
	[Export] public Vector3 RingCenterLocalOffset { get; set; } = Vector3.Zero;
	[Export(PropertyHint.Range, "-1000000.0,1000000.0,0.1")] public float RingAcrossBaseOffsetMeters { get; set; } = 0.0f;
	[Export] public bool EnforceRingAcrossBounds { get; set; } = false;
	[Export(PropertyHint.Range, "1.0,1000000.0,1.0")] public float RingAcrossWidthMeters { get; set; } = 20000.0f;
	[Export(PropertyHint.Range, "1000.0,50000.0,100.0")] public float StructuralDepthMeters { get; set; } = 22300.0f;
	[Export(PropertyHint.Range, "0.0,10000.0,100.0")] public float StructuralWallClearanceMeters { get; set; } = 2000.0f;
	[Export] public bool GenerateStructuralBedrockEdgeChunks { get; set; } = false;
	[Export] public bool GenerateStructuralBedrockUndersideChunks { get; set; } = false;
	[Export(PropertyHint.Range, "-1000000.0,1000000.0,0.1")] public float RingVerticalBaseOffsetMeters { get; set; } = 0.0f;
	[ExportGroup("Ring Hemisphere Lighting")]
	[Export] public bool EnableRingHemisphereLighting { get; set; } = true;
	[Export] public NodePath RingSunLightPath { get; set; } = new("../StarLight");
	[Export(PropertyHint.Range, "-1.0,1.0,0.01")] public float RingDayStartDot { get; set; } = -0.2f;
	[Export(PropertyHint.Range, "-1.0,1.0,0.01")] public float RingDayFullDot { get; set; } = 0.35f;
	[Export] public Color RingNightTint { get; set; } = new(0.30f, 0.45f, 0.70f, 1.0f);
	[Export(PropertyHint.Range, "0.0,1.0,0.01")] public float RingNightDirectLightFactor { get; set; } = 0.0f;
	[Export(PropertyHint.Range, "0.0,2.0,0.01")] public float RingDayDirectLightFactor { get; set; } = 1.0f;
	[Export(PropertyHint.Range, "0.0,1.0,0.01")] public float RingShadowFloor { get; set; } = 0.0f;
	[Export] public bool EnableCurvatureVertexWarp { get; set; } = false;
	[Export] public bool UseCameraRelativeTopLevelChunks { get; set; } = true;
	[Export(PropertyHint.Range, "0.0,1.0,0.01")] public float CurvatureVertexWarpStrength { get; set; } = 1.0f;
	[Export] public bool EnableCurvatureWarpHotkeys { get; set; } = true;
	[Export(PropertyHint.Range, "0.01,0.5,0.01")] public float CurvatureWarpHotkeyStep { get; set; } = 0.05f;
	[Export(PropertyHint.Range, "0.0,1.0,0.01")] public float CurvatureWarpHotkeyResetValue { get; set; } = 1.0f;
	[Export] public Key CurvatureWarpIncreaseKey { get; set; } = Key.I;
	[Export] public Key CurvatureWarpDecreaseKey { get; set; } = Key.U;
	[Export] public Key CurvatureWarpResetKey { get; set; } = Key.O;
	[Export] public bool LogCurvatureWarpHotkeyChanges { get; set; } = true;
	[Export] public bool EnableSeamGapDebugVisualization { get; set; } = false;
	[Export(PropertyHint.Range, "0.02,1.0,0.01")] public float SeamGapDebugUpdateIntervalSeconds { get; set; } = 0.2f;
	[Export(PropertyHint.Range, "2,64,1")] public int SeamGapDebugSamplesAcross { get; set; } = 12;
	[Export(PropertyHint.Range, "2,16,1")] public int SeamGapDebugSamplesVertical { get; set; } = 3;
	[Export(PropertyHint.Range, "0.01,5.0,0.01")] public float SeamGapDebugColorFullScaleMeters { get; set; } = 0.5f;
	[Export] public bool LogSeamGapDebugStats { get; set; } = false;
	[Export] public bool RunAroundRingSeamSelfTestsOnReady { get; set; } = false;

	[Export] public bool EnableFloatingOriginBridge { get; set; } = false;
	[Export] public bool CompensateBridgeForManagerGlobalMotion { get; set; } = true;
	[Export] public bool EnableCanonicalFloatingOriginAnchor { get; set; } = false;

	private readonly Dictionary<RingChunkCoordinate, VoxelChunkNode> _chunks = new();
	private ulong _voxelVisualCoverageRevision;
	private readonly Dictionary<RingChunkCoordinate, double> _queuedRebuildDeadlines = new();
	private readonly Dictionary<RingChunkCoordinate, Dictionary<int, ushort>> _chunkOverrides = new();
	// Sparse, authoritative construction tops for systems (rain/shelter) which
	// must not search the legal vertical build range on a frame query.
	private readonly Dictionary<RingTerrainColumnCoordinate, RainColumnHeights> _rainColumnTops = new();
	private readonly HashSet<RingChunkCoordinate> _dirtyOverrideChunks = new();
	private readonly HashSet<RingChunkCoordinate> _knownEmptyStreamingChunks = new();
	private readonly HashSet<RingChunkCoordinate> _knownSolidStreamingChunks = new();
	private readonly Dictionary<RingTerrainColumnCoordinate, int[]> _baselineSurfaceTileCache = new();
	private readonly Queue<RingTerrainColumnCoordinate> _baselineSurfaceTileOrder = new();
	private readonly object _baselineSurfaceTileLock = new();
	private readonly Queue<RingChunkCoordinate> _pendingChunkLoads = new();
	private readonly HashSet<RingChunkCoordinate> _pendingChunkLoadSet = new();
	private readonly Queue<VoxelChunkNode> _chunkPool = new();
	private HashSet<RingChunkCoordinate> _streamingDesiredVisual = new();
	private HashSet<RingChunkCoordinate> _streamingDesiredCollision = new();
	private RingTerrainSampler? _terrainSampler;
	public RingTerrainSampler? TerrainSampler => _terrainSampler;
	/// <summary>Public immutable sampler handoff for opt-in terrain backends.</summary>
	public RingTerrainSampler? CreateIndependentTerrainSampler()
	{
		EnsureTerrainSampler();
		return _terrainSampler?.CreateIndependentCopy();
	}
	private long? _runtimeAroundRingChunkLength;
	private double? _runtimeRingRadiusMeters;
	private Node3D? _streamingFocusNode;
	private RingVoxelExplorerController? _streamingVelocityProvider;
	private RingCanonicalPosition? _streamingCanonicalFocusOverride;
	private RingCanonicalPosition? _floatingOriginCanonicalAnchor;
	private double _streamingUpdateAccumulator;
	private bool _hasLastStreamingCenter;
	private int _lastStreamingVisualVerticalRadius = int.MinValue;
	private int _lastStreamingCollisionVerticalRadius = int.MinValue;
	private bool _surfaceResidencyActive;
	private bool _automaticSurfaceStreamingSuspended;
	private bool _externalMeshMemoryPressure;
	private bool _voxelDataMemoryPressure;
	private RingChunkCoordinate _lastStreamingCenter;
	private RingChunkCoordinate _lastPredictedStreamingCenter;
	private bool _fastTravelStreamingActive;
	private bool _fastTravelChunkMeshesHidden;
	private double _fastTravelStoppedElapsedSeconds;
	private Task<StreamingWindowPlan>? _streamingPlanTask;
	private StreamingWindowRequest? _queuedStreamingPlan;
	private ulong _streamingPlanGeneration;
	private Vector3 _floatingOriginWorldOffset = Vector3.Zero;
	private Vector3 _lastManagerGlobalPosition;
	private bool _hasLastManagerGlobalPosition;
	private MeshInstance3D? _seamGapDebugMeshInstance;
	private ImmediateMesh? _seamGapDebugMesh;
	private StandardMaterial3D? _seamGapDebugMaterial;
	private double _seamGapDebugAccumulator;
	private float _lastLoggedSeamMaxGapMeters = -1.0f;
	private DirectionalLight3D? _ringSunLight;
	private ShaderMaterial? _voxelRingMaterial;
	private Vector3 _lastVoxelMaterialRingCenter = new(float.PositiveInfinity, 0.0f, 0.0f);
	private Vector3 _lastVoxelMaterialCameraPosition = new(float.PositiveInfinity, 0.0f, 0.0f);
	private Vector3 _lastVoxelMaterialSunDirection = new(float.PositiveInfinity, 0.0f, 0.0f);
	private VoxelMaterialLightingConfiguration _lastVoxelMaterialLightingConfiguration;
	private bool _hasVoxelMaterialLightingConfiguration;
	private ulong _lodSurfaceEditVersion = 1;
	private ulong _rainSurfaceEditVersion = 1;
	private RingLodSurfaceEditSnapshot? _cachedLodSurfaceEditSnapshot;
	private readonly Dictionary<RingTerrainSampleCoordinate, float> _naturalTerrainSdfDeltas = new();
	private readonly HashSet<RingTerrainSampleCoordinate> _dirtyNaturalTerrainSdfSamples = new();
	private RingNaturalTerrainSdfEditSnapshot? _cachedNaturalTerrainSdfEditSnapshot;

	private readonly record struct VoxelMaterialLightingConfiguration(
		float DayStartDot,
		float DayFullDot,
		Vector3 NightTint,
		float NightDirectLightFactor,
		float DayDirectLightFactor,
		float ShadowFloor,
		bool PilotTriplanarEnabled,
		float PilotTextureScale);

	public int LoadedChunkCount => _chunks.Count;
	public ulong VoxelVisualCoverageRevision => _voxelVisualCoverageRevision;
	public int PendingChunkLoadCount => _pendingChunkLoads.Count;
	public int PendingChunkRebuildCount =>
		_queuedRebuildDeadlines.Count + _pendingResidentVoxelRebuilds.Count;
	public int PooledChunkCount => _chunkPool.Count;
	public int KnownEmptyStreamingChunkCount => _knownEmptyStreamingChunks.Count;
	public int KnownSolidStreamingChunkCount => _knownSolidStreamingChunks.Count;
	public int DesiredVisualChunkCount => _streamingDesiredVisual.Count;
	public int DesiredCollisionChunkCount => _streamingDesiredCollision.Count;
	/// <summary>True while the automatic player focus is close enough to need local voxel presentation.</summary>
	public bool SurfaceResidencyActive => _surfaceResidencyActive;
	public bool VoxelMeshMemoryPressureActive => _externalMeshMemoryPressure || _voxelDataMemoryPressure;
	public int RetainedVoxelDataChunkCount
	{
		get
		{
			int count = 0;
			foreach (VoxelChunkNode chunk in _chunks.Values)
				if (chunk.HasRetainedVoxelData) count++;
			return count;
		}
	}

	public void SetExternalMeshMemoryPressure(bool active)
	{
		_externalMeshMemoryPressure = active;
	}

	private void SetVoxelDataMemoryPressure(bool active)
	{
		_voxelDataMemoryPressure = active;
	}
	public long EstimatedVoxelDataBytes => (long)RetainedVoxelDataChunkCount
		* VoxelConstants.ChunkSizeX
		* VoxelConstants.ChunkSizeY
		* VoxelConstants.ChunkSizeZ
		* sizeof(byte);
	public float LastPredictionDistanceMeters { get; private set; }
	public float LastBrakingDistanceMeters { get; private set; }
	public ulong LodSurfaceEditVersion => _lodSurfaceEditVersion;
	/// <summary>Changes for every retained voxel edit, including roofs whose natural surface height is unchanged.</summary>
	public ulong RainSurfaceEditVersion => _rainSurfaceEditVersion;
	public bool HasStreamingCanonicalFocusOverride => _streamingCanonicalFocusOverride.HasValue;
	public bool TryGetStreamingCanonicalFocusOverride(out RingCanonicalPosition position)
	{
		if (_streamingCanonicalFocusOverride is { } overridePosition)
		{
			position = overridePosition;
			return true;
		}
		position = default;
		return false;
	}
	public event Action<RingLodGeometrySnapshot, RingLodGeometrySnapshot, RingCanonicalPosition>?
		CanonicalFloatingOriginAnchorChanged;

	public void SetFloatingOriginCanonicalAnchor(in RingCanonicalPosition position)
	{
		if (!EnableCanonicalFloatingOriginAnchor)
		{
			ClearFloatingOriginCanonicalAnchor();
			return;
		}

		double circumference = GetCanonicalRingCircumferenceMeters64();
		RingCanonicalPosition wrappedAnchor = position.Wrap(circumference);
		RingCanonicalPosition anchor = wrappedAnchor;
		if (_floatingOriginCanonicalAnchor is { } previousAnchor)
		{
			// Keep the presentation anchor on a continuous, unwrapped arc. The
			// authoritative player coordinate still wraps normally, but crossing
			// zero must not look like a circumference-sized origin shift to every
			// chunk and proxy subscriber.
			double nearestTurn = Math.Round(
				(previousAnchor.AroundMeters - wrappedAnchor.AroundMeters)
				/ Math.Max(1.0, circumference));
			anchor = new RingCanonicalPosition(
				wrappedAnchor.AroundMeters + nearestTurn * circumference,
				wrappedAnchor.VerticalMeters,
				wrappedAnchor.AcrossMeters);
		}
		if (_floatingOriginCanonicalAnchor is { } current
			&& Math.Abs(current.AroundMeters - anchor.AroundMeters) < 0.001
			&& Math.Abs(current.VerticalMeters - anchor.VerticalMeters) < 0.001
			&& Math.Abs(current.AcrossMeters - anchor.AcrossMeters) < 0.001)
		{
			return;
		}

		RingLodGeometrySnapshot previousGeometry = CaptureLodGeometrySnapshot();
		_floatingOriginCanonicalAnchor = anchor;
		RefreshPrecisionSafeChunkTransforms();
		CanonicalFloatingOriginAnchorChanged?.Invoke(
			previousGeometry,
			CaptureLodGeometrySnapshot(),
			anchor);
	}

	public void ClearFloatingOriginCanonicalAnchor()
	{
		if (!_floatingOriginCanonicalAnchor.HasValue)
		{
			return;
		}

		RingLodGeometrySnapshot previousGeometry = CaptureLodGeometrySnapshot();
		_floatingOriginCanonicalAnchor = null;
		RefreshPrecisionSafeChunkTransforms();
		CanonicalFloatingOriginAnchorChanged?.Invoke(
			previousGeometry,
			CaptureLodGeometrySnapshot(),
			default);
	}

	public void ConfigureRuntimeRingTopology(long aroundRingChunkLength, double ringRadiusMeters)
	{
		if (aroundRingChunkLength < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(aroundRingChunkLength));
		}

		if (ringRadiusMeters <= 0.0 || double.IsNaN(ringRadiusMeters) || double.IsInfinity(ringRadiusMeters))
		{
			throw new ArgumentOutOfRangeException(nameof(ringRadiusMeters));
		}

		_runtimeAroundRingChunkLength = aroundRingChunkLength;
		_runtimeRingRadiusMeters = ringRadiusMeters;
		RebuildTerrainNoises();
		InvalidateLodSurfaceEditSnapshot();
		_hasLastStreamingCenter = false;
	}

	public void ClearRuntimeRingTopology()
	{
		if (!_runtimeAroundRingChunkLength.HasValue && !_runtimeRingRadiusMeters.HasValue)
		{
			return;
		}

		_runtimeAroundRingChunkLength = null;
		_runtimeRingRadiusMeters = null;
		RebuildTerrainNoises();
		InvalidateLodSurfaceEditSnapshot();
		_hasLastStreamingCenter = false;
	}

	/// <summary>
	/// Rebuilds the shared deterministic sampler after a launcher has assigned a
	/// duplicated terrain resource or changed the runtime ring dimensions.
	/// </summary>
	public void RefreshRuntimeTerrainDefinition()
	{
		RebuildTerrainNoises();
		InvalidateLodSurfaceEditSnapshot();
	}

	public bool FastTravelStreamingActive => _fastTravelStreamingActive;
	public bool StreamingPlanPending =>
		_streamingPlanTask != null || _queuedStreamingPlan != null;

	/// <summary>
	/// Captures the terrain definition used by chunks and regional LOD workers.
	/// It is intentionally independent from loaded chunk data so a future coarse
	/// Transvoxel neighbor can be sampled even when its proxy is still streaming.
	/// </summary>
	public RingTerrainSdfSnapshot? CaptureTerrainSdfSnapshot()
	{
		if (_terrainSampler == null || TerrainSettings == null)
			return null;
		return RingTerrainSdfSnapshot.Create(
			_terrainSampler,
			CaptureLodSurfaceEditSnapshot(),
			CaptureNaturalTerrainSdfEditSnapshot(),
			Mathf.RoundToInt(RingAcrossWidthMeters),
			TerrainSettings.BedrockY,
			TerrainSettings.SoilDepthVoxels);
	}

	public RingTerrainCoarseSdfChunk? CaptureCoarseTerrainSdfChunk(
		in RingChunkCoordinate fineChunkCoordinate,
		int spacingMeters = 2)
	{
		RingTerrainSdfSnapshot? snapshot = CaptureTerrainSdfSnapshot();
		if (snapshot == null) return null;
		using (snapshot)
		{
			int spacing = Math.Max(1, spacingMeters);
			int coarseSpan = RingTerrainCoarseSdfChunk.CellCount * spacing;
			RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(fineChunkCoordinate);
			var fineOrigin = new RingTerrainSampleCoordinate(
				canonical.AroundRingChunk * VoxelConstants.ChunkSizeX,
				canonical.VerticalChunk * VoxelConstants.ChunkSizeY,
				canonical.AcrossRingChunk * VoxelConstants.ChunkSizeZ);
			var coarseOrigin = new RingTerrainSampleCoordinate(
				FloorToMultiple(fineOrigin.AroundVoxel, coarseSpan),
				FloorToMultiple(fineOrigin.VerticalVoxel, coarseSpan),
				FloorToMultiple(fineOrigin.AcrossVoxel, coarseSpan));
			return RingTerrainCoarseSdfChunk.Build(snapshot, coarseOrigin, spacing);
		}
	}

	public RingTerrainFineSdfHalo? CaptureFineTerrainSdfHalo(
		in RingChunkCoordinate fineChunkCoordinate)
	{
		RingTerrainSdfSnapshot? snapshot = CaptureTerrainSdfSnapshot();
		if (snapshot == null) return null;
		using (snapshot)
		{
			RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(fineChunkCoordinate);
			var fineOrigin = new RingTerrainSampleCoordinate(
				canonical.AroundRingChunk * VoxelConstants.ChunkSizeX,
				canonical.VerticalChunk * VoxelConstants.ChunkSizeY,
				canonical.AcrossRingChunk * VoxelConstants.ChunkSizeZ);
			return RingTerrainFineSdfHalo.Build(snapshot, fineOrigin);
		}
	}

	public RingNaturalTerrainSdfEditSnapshot CaptureNaturalTerrainSdfEditSnapshot()
	{
		if (_cachedNaturalTerrainSdfEditSnapshot != null
			&& _cachedNaturalTerrainSdfEditSnapshot.Version == _lodSurfaceEditVersion)
		{
			return _cachedNaturalTerrainSdfEditSnapshot;
		}

		_cachedNaturalTerrainSdfEditSnapshot = new RingNaturalTerrainSdfEditSnapshot(
			_lodSurfaceEditVersion,
			GetAroundRingChunkLength() * VoxelConstants.ChunkSizeX,
			_naturalTerrainSdfDeltas);
		return _cachedNaturalTerrainSdfEditSnapshot;
	}

	public bool RunTerrainSdfContractSelfTest(out string summary)
	{
		EnsureTerrainSampler();
		if (_terrainSampler == null || TerrainSettings == null)
		{
			summary = "deterministic terrain is unavailable";
			return false;
		}

		RingLodSurfaceEditSnapshot edits = CaptureLodSurfaceEditSnapshot();
		int around = 0;
		int across = Mathf.Clamp(
			Mathf.RoundToInt(RingAcrossWidthMeters * 0.5f),
			0,
			Mathf.RoundToInt(RingAcrossWidthMeters));
		int surface = edits.GetSurfaceHeight(_terrainSampler, around, across);
		using RingTerrainSdfSnapshot snapshot = CaptureTerrainSdfSnapshot()!;
		float below = snapshot.SampleDensity(around, surface, across);
		float above = snapshot.SampleDensity(around, surface + 1, across);
		if (below >= 0.0f
			|| above <= 0.0f
			|| !Mathf.IsEqualApprox(above - below, 1.0f))
		{
			summary = $"surface sign mismatch: below={below:0.###}, above={above:0.###}";
			return false;
		}
		if (snapshot.SampleNaturalMaterial(around, surface + 1, across) != BlockType.Air
			|| snapshot.SampleNaturalMaterial(around, surface, across) == BlockType.Air)
		{
			summary = "material boundary disagrees with the density boundary";
			return false;
		}

		float[] coarse = snapshot.CaptureDensityLattice(
			new Vector3I(around, surface, across),
			new Vector3I(2, 2, 2),
			spacingMeters: 2);
		if (coarse.Length != 8
			|| coarse[0] >= 0.0f
			|| coarse[2] <= 0.0f
			|| !Mathf.IsEqualApprox(coarse[2] - coarse[0], 2.0f))
		{
			summary = "2m density lattice has an invalid vertical sampling order";
			return false;
		}

		summary = $"surface={surface}m, fine=[{below:0.0},{above:0.0}], coarseSamples={coarse.Length}";
		return true;
	}

	public Godot.Collections.Dictionary CaptureTerrainVariationAudit(
		long centerAroundVoxel,
		int centerAcrossVoxel,
		int sampleSpacingMeters = 20_000_000,
		int halfSampleCount = 12)
	{
		EnsureTerrainSampler();
		var audit = new Godot.Collections.Dictionary();
		if (_terrainSampler == null)
		{
			audit["available"] = false;
			return audit;
		}

		int spacing = Math.Max(1, sampleSpacingMeters);
		int halfSamples = Math.Max(1, halfSampleCount);
		int minimumHeight = int.MaxValue;
		int maximumHeight = int.MinValue;
		int waterSamples = 0;
		int totalSamples = 0;
		for (int acrossIndex = -halfSamples; acrossIndex <= halfSamples; acrossIndex++)
		for (int aroundIndex = -halfSamples; aroundIndex <= halfSamples; aroundIndex++)
		{
			long around = centerAroundVoxel + aroundIndex * (long)spacing;
			int across = Mathf.Clamp(
				centerAcrossVoxel + acrossIndex * spacing,
				0,
				Mathf.Max(0, Mathf.RoundToInt(RingAcrossWidthMeters) - 1));
			TerrainSample sample = _terrainSampler.GetTerrainSample(around, across);
			minimumHeight = Math.Min(minimumHeight, sample.SurfaceHeight);
			maximumHeight = Math.Max(maximumHeight, sample.SurfaceHeight);
			waterSamples += sample.HasSurfaceWater ? 1 : 0;
			totalSamples++;
		}

		audit["available"] = true;
		audit["minimum_height"] = minimumHeight;
		audit["maximum_height"] = maximumHeight;
		audit["height_range"] = maximumHeight - minimumHeight;
		audit["water_samples"] = waterSamples;
		audit["total_samples"] = totalSamples;
		return audit;
	}

	private static bool RunMegaRingSamplerSelfTest(out string summary)
	{
		if (!MegaRingScale.Validate(out string scaleError))
		{
			summary = scaleError;
			return false;
		}

		if (!RingLodGeometrySnapshot.RunFloatingOriginSelfTest())
		{
			summary = "camera-relative Mega Ring geometry lost local precision";
			return false;
		}

		var settings = new VoxelTerrainSettings();
		using var sampler = new RingTerrainSampler(
			settings,
			periodicAroundRing: true,
			MegaRingScale.AroundVoxelLength,
			MegaRingScale.EffectiveRadiusMeters,
			checked((int)MegaRingScale.RingWidthMeters),
			VoxelConstants.MaxVertical);

		const int acrossVoxel = 4096;
		long circumference = MegaRingScale.AroundVoxelLength;
		int seamStart = sampler.GetSurfaceHeight(0L, acrossVoxel);
		int seamEnd = sampler.GetSurfaceHeight(circumference, acrossVoxel);
		int negativeOne = sampler.GetSurfaceHeight(-1L, acrossVoxel);
		int finalVoxel = sampler.GetSurfaceHeight(circumference - 1L, acrossVoxel);
		int halfway = sampler.GetSurfaceHeight(circumference / 2L, acrossVoxel);
		int wrappedHalfway = sampler.GetSurfaceHeight(circumference + circumference / 2L, acrossVoxel);

		if (seamStart != seamEnd || negativeOne != finalVoxel || halfway != wrappedHalfway)
		{
			summary = "periodic terrain samples disagree at the Mega Ring seam";
			return false;
		}

		RingChunkCoordinate finalChunk = RingCoordMath.ToChunkCoordinate(
			new RingVoxelCoordinate(circumference - 1L, acrossVoxel, 0),
			circumference);
		if (finalChunk.AroundRingChunk != MegaRingScale.AroundChunkCount - 1L)
		{
			summary = "final Mega Ring voxel resolved to the wrong chunk";
			return false;
		}

		summary = $"circumference={circumference}m, finalChunk={finalChunk.AroundRingChunk}, seam={seamStart}";
		return true;
	}

	public bool RunTerrainSdfTransitionContractSelfTest(out string summary)
	{
		EnsureTerrainSampler();
		if (_terrainSampler == null || TerrainSettings == null)
		{
			summary = "deterministic terrain is unavailable";
			return false;
		}

		int acrossChunk = Mathf.FloorToInt(RingAcrossWidthMeters * 0.5f)
			/ VoxelConstants.ChunkSizeZ;
		var fineCoordinate = new RingChunkCoordinate(0, acrossChunk, 0);
		RingTerrainCoarseSdfChunk? chunk = CaptureCoarseTerrainSdfChunk(fineCoordinate);
		RingTerrainFineSdfHalo? halo = CaptureFineTerrainSdfHalo(fineCoordinate);
		if (chunk == null || halo == null)
		{
			summary = "transition SDF capture failed";
			return false;
		}

		using RingTerrainSdfSnapshot snapshot = CaptureTerrainSdfSnapshot()!;
		for (int z = 0; z < RingTerrainCoarseSdfChunk.SampleCount; z += 8)
		for (int y = 0; y < RingTerrainCoarseSdfChunk.SampleCount; y += 8)
		for (int x = 0; x < RingTerrainCoarseSdfChunk.SampleCount; x += 8)
		{
			long around = chunk.GlobalOrigin.X + x * chunk.SpacingMeters;
			int vertical = chunk.GlobalOrigin.Y + y * chunk.SpacingMeters;
			int across = chunk.GlobalOrigin.Z + z * chunk.SpacingMeters;
			if (!Mathf.IsEqualApprox(
				chunk.GetDensity(x, y, z),
				snapshot.SampleDensity(around, vertical, across))
				|| chunk.GetMaterial(x, y, z) != snapshot.SampleNaturalMaterial(around, vertical, across))
			{
				summary = $"coarse sample mismatch at ({x},{y},{z})";
				return false;
			}
		}

		int boundary = RingTerrainCoarseSdfChunk.SampleCount - 1;
		float coarseBoundary = chunk.GetDensity(boundary, 0, 0);
		float adjacentFineBoundary = snapshot.SampleDensity(
			chunk.GlobalOrigin.X + RingTerrainCoarseSdfChunk.CellCount * chunk.SpacingMeters,
			chunk.GlobalOrigin.Y,
			chunk.GlobalOrigin.Z);
		if (!Mathf.IsEqualApprox(coarseBoundary, adjacentFineBoundary))
		{
			summary = "coarse/fine boundary density disagrees";
			return false;
		}

		int haloLast = RingTerrainFineSdfHalo.SampleCount - 1;
		if (!Mathf.IsEqualApprox(
				halo.GetDensity(0, 0, 0),
				snapshot.SampleDensity(halo.GlobalOrigin.X, halo.GlobalOrigin.Y, halo.GlobalOrigin.Z))
			|| !Mathf.IsEqualApprox(
				halo.GetDensity(haloLast, haloLast, haloLast),
				snapshot.SampleDensity(
					halo.GlobalOrigin.X + haloLast,
					halo.GlobalOrigin.Y + haloLast,
					halo.GlobalOrigin.Z + haloLast)))
		{
			summary = "fine Transvoxel padding disagrees with the terrain SDF";
			return false;
		}

		summary = $"coarse={chunk.Densities.Length}@{chunk.SpacingMeters}m, fineHalo={halo.Densities.Length}";
		return true;
	}

	private static int FloorToMultiple(int value, int multiple)
	{
		int remainder = value % multiple;
		return remainder < 0 ? value - remainder - multiple : value - remainder;
	}

	private static long FloorToMultiple(long value, int multiple)
	{
		long remainder = value % multiple;
		return remainder < 0 ? value - remainder - multiple : value - remainder;
	}

	private sealed record StreamingWindowRequest(
		RingCanonicalPosition CanonicalFocus,
		RingChunkCoordinate Center,
		RingChunkCoordinate PredictedCenter,
		Vector3 PredictionOffset,
		HashSet<RingChunkCoordinate> Loaded,
		HashSet<RingChunkCoordinate> Pending,
		HashSet<RingChunkCoordinate> KnownEmpty,
		HashSet<RingChunkCoordinate> KnownSolid,
		int VisualRadiusAround,
		int VisualRadiusAcross,
		int VisualRadiusVertical,
		int CollisionRadiusAround,
		int CollisionRadiusAcross,
		int CollisionRadiusVertical,
		ulong Generation);

	private sealed record StreamingWindowPlan(
		HashSet<RingChunkCoordinate> DesiredVisual,
		HashSet<RingChunkCoordinate> DesiredCollision,
		RingChunkCoordinate[] PrioritizedMissing,
		RingChunkCoordinate[] Departed,
		ulong Generation);

	public override void _Ready()
	{
		_fluids = new FluidRuntime(this);
		ApplyAsyncVoxelMeshingCommandLineOverrides();

		string[] userArguments = OS.GetCmdlineUserArgs();
		if (Array.Exists(
			userArguments,
			static argument => argument.Equals("--smooth-terrain-pilot", StringComparison.OrdinalIgnoreCase)))
		{
			UseSmoothNaturalTerrainPilot = true;
			GD.Print("Hybrid terrain pilot enabled: natural terrain is smooth; Concrete and Glass remain block-meshed.");
		}
		VoxelChunkMesherNative.InitializeFromProject(userArguments);
		bool runMesherSelfTest = Array.Exists(
			userArguments,
			static argument => argument.Equals("--voxel-mesher-self-test", StringComparison.OrdinalIgnoreCase));
		bool runMesherBenchmark = Array.Exists(
			userArguments,
			static argument => argument.Equals("--voxel-mesher-benchmark", StringComparison.OrdinalIgnoreCase));
		bool runTransvoxelTransitionSelfTest = Array.Exists(
			userArguments,
			static argument => argument.Equals("--voxel-transvoxel-self-test", StringComparison.OrdinalIgnoreCase));
		bool runTerrainSdfSelfTest = Array.Exists(
			userArguments,
			static argument => argument.Equals("--terrain-sdf-self-test", StringComparison.OrdinalIgnoreCase));
			bool runTerrainSdfTransitionSelfTest = Array.Exists(
				userArguments,
				static argument => argument.Equals("--terrain-sdf-transition-self-test", StringComparison.OrdinalIgnoreCase));
		bool runMegaRingSamplerSelfTest = Array.Exists(
			userArguments,
			static argument => argument.Equals("--mega-ring-sampler-self-test", StringComparison.OrdinalIgnoreCase));
		bool runStreamingResidencySelfTest = Array.Exists(
			userArguments,
			static argument => argument.Equals("--voxel-streaming-residency-self-test", StringComparison.OrdinalIgnoreCase));
		if (runMesherSelfTest || runMesherBenchmark || runTransvoxelTransitionSelfTest)
		{
			bool passed = runTransvoxelTransitionSelfTest
				? VoxelChunkMesherDiagnostics.RunTransvoxelTransitionSelfTest(out string summary)
				: VoxelChunkMesherDiagnostics.RunSelfTest(out summary);
			if (passed)
				GD.Print($"Voxel {(runTransvoxelTransitionSelfTest ? "Transvoxel transition " : "mesher ")}self-test passed: {summary}");
			else
				GD.PushError($"Voxel {(runTransvoxelTransitionSelfTest ? "Transvoxel transition " : "mesher ")}self-test failed: {summary}");

			if (passed && runMesherBenchmark)
			{
				int iterations = 64;
				foreach (string argument in userArguments)
				{
					const string prefix = "--voxel-mesher-iterations=";
					if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
						&& int.TryParse(argument[prefix.Length..], out int requested))
					{
						iterations = Math.Clamp(requested, 1, 4096);
					}
				}
				GD.Print($"Voxel mesher benchmark: {VoxelChunkMesherDiagnostics.RunBenchmark(iterations, forceManaged: true)}");
				if (VoxelChunkMesherNative.IsAvailable)
					GD.Print($"Voxel mesher benchmark: {VoxelChunkMesherDiagnostics.RunBenchmark(iterations)}");
			}

			GetTree().Quit(passed ? 0 : 1);
			return;
		}

		if (runMegaRingSamplerSelfTest)
		{
			bool passed = RunMegaRingSamplerSelfTest(out string summary);
			if (passed)
				GD.Print($"Mega Ring sampler self-test passed: {summary}");
			else
				GD.PushError($"Mega Ring sampler self-test failed: {summary}");
			GetTree().Quit(passed ? 0 : 1);
			return;
		}

		if (runStreamingResidencySelfTest)
		{
			bool passed = RunStreamingResidencySelfTest(out string summary);
			if (passed)
				GD.Print($"Voxel streaming residency self-test passed: {summary}");
			else
				GD.PushError($"Voxel streaming residency self-test failed: {summary}");
			GetTree().Quit(passed ? 0 : 1);
			return;
		}

		_lastManagerGlobalPosition = GlobalPosition;
		_hasLastManagerGlobalPosition = true;
		if (DormantEditableBackend)
		{
			// The sampler remains lazily available to smooth terrain and proxy LODs.
			// Do not register the floating-origin bridge, allocate chunk pools, or
			// schedule legacy editable work while the replacement is selected.
			return;
		}

		if (EnableFloatingOriginBridge)
		{
			AddToGroup(FloatingOriginBridgeGroup);
		}

		if (BuildOnReady)
		{
			RebuildGrid();
		}
		PrewarmVoxelStreamingResources();

		if (runTerrainSdfSelfTest || runTerrainSdfTransitionSelfTest)
		{
			bool passed = runTerrainSdfTransitionSelfTest
				? RunTerrainSdfTransitionContractSelfTest(out string summary)
				: RunTerrainSdfContractSelfTest(out summary);
			if (passed)
				GD.Print($"Terrain SDF {(runTerrainSdfTransitionSelfTest ? "transition " : string.Empty)}self-test passed: {summary}");
			else
				GD.PushError($"Terrain SDF {(runTerrainSdfTransitionSelfTest ? "transition " : string.Empty)}self-test failed: {summary}");
			GetTree().Quit(passed ? 0 : 1);
			return;
		}

		if (RunAroundRingSeamSelfTestsOnReady)
		{
			bool seamOk = RunAroundRingSeamSelfTests();
			if (!seamOk)
			{
				GD.PushWarning("VoxelChunkGridManager seam wrap self-test failed.");
			}
			else
			{
				GD.Print($"Canonical streaming self-test passed: chunks={GetAroundRingChunkLength()}, circumference={GetCanonicalRingCircumferenceMeters64():0}m");
				double atmosphereRadius = GetCanonicalRingCircumferenceMeters64() / Math.Tau;
				if (RingAtmosphereModel.RunContractSelfTest(atmosphereRadius, out string atmosphereSummary))
					GD.Print($"Ring-local atmosphere self-test passed: {atmosphereSummary}");
				else
					GD.PushError($"Ring-local atmosphere self-test failed: {atmosphereSummary}");
				if (_terrainSampler != null)
				{
					var summit = _terrainSampler.ExceptionalSummitCanonical;
					GD.Print($"Terrain elevation contract: sea={_terrainSampler.SeaLevelY}m, routinePeak<={_terrainSampler.RoutineMountainCeilingY}m, exceptionalPeak={_terrainSampler.ExceptionalSummitSurfaceHeight}m at canonical ({summit.Around},{summit.Across}), maximum={_terrainSampler.MaximumPossibleSurfaceHeight}m");
				}
			}
		}
	}

	private void PrewarmVoxelStreamingResources()
	{
		int target = Mathf.Max(0, PrewarmedVoxelChunkNodes);
		if (!EnableChunkStreaming || target == 0) return;
		Material? material = GetVoxelChunkMaterial();
		while (_chunkPool.Count < target)
		{
			var chunk = new VoxelChunkNode
			{
				Name = $"PrewarmedVoxelChunk_{_chunkPool.Count}",
				GenerateOnReady = false,
				InitialSolidHeight = InitialSolidHeight,
				BlockColorMap = BlockColorMap,
				Visible = false
			};
			AddChild(chunk);
			chunk.PrepareForReuse(buildCollision: false);
			chunk.SetSharedMaterialOverride(material);
			chunk.PrepareForPool();
			_chunkPool.Enqueue(chunk);
		}
	}

	public override void _ExitTree()
	{
		ShutdownAsyncVoxelMeshing();
		while (_chunkPool.Count > 0)
		{
			VoxelChunkNode pooled = _chunkPool.Dequeue();
			if (GodotObject.IsInstanceValid(pooled))
				pooled.Free();
		}
		_terrainSampler?.Dispose();
		_terrainSampler = null;
	}

	public override void _Process(double delta)
	{
		using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Voxel update (inclusive)");
		if (DormantEditableBackend) return;
		UpdateFloatingOriginBridgeFromManagerMotion();
		UpdateVoxelRingLightingMaterial();
		ServiceCompletedVoxelBuilds();
		UpdateFastTravelStreamingState(delta);
		ServiceCompletedStreamingPlan();
		if (!_fastTravelStreamingActive || _streamingCanonicalFocusOverride.HasValue)
			ProcessIncrementalChunkLoads();

		if (EnableChunkStreaming
			&& (!_fastTravelStreamingActive
				|| _streamingCanonicalFocusOverride.HasValue))
		{
			_streamingUpdateAccumulator += delta;
			double interval = Math.Max(0.01, StreamingUpdateIntervalSeconds);
			if (_streamingUpdateAccumulator >= interval)
			{
				_streamingUpdateAccumulator = 0.0;
				UpdateStreamingWindow(force: false);
			}
		}

		ProcessRebuildQueue();
		ProcessDynamicWater(delta);
		UpdateSeamGapDebugVisualization(delta);
		ProcessPhase5Runtime(delta);
	}

	private void UpdateFastTravelStreamingState(double delta)
	{
		ResolveStreamingVelocityProvider();
		bool eligible = SuspendLocalVoxelStreamingDuringFastTravel
			&& !_streamingCanonicalFocusOverride.HasValue
			&& _streamingVelocityProvider is
				{
					NoclipEnabled: true
				};
		float actualSpeed = _streamingVelocityProvider?.ActualTravelSpeedMetersPerSecond ?? 0.0f;
		bool next = false;
		if (eligible)
		{
			if (_fastTravelStreamingActive)
			{
				float stoppedThreshold = Mathf.Max(0.0f, FastTravelStoppedSpeedThresholdMetersPerSecond);
				if (actualSpeed <= stoppedThreshold)
				{
					_fastTravelStoppedElapsedSeconds += Mathf.Max(0.0, delta);
					next = _fastTravelStoppedElapsedSeconds
						< Mathf.Max(0.0f, FastTravelResumeStoppedSeconds);
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
				next = actualSpeed
					>= Mathf.Max(100.0f, FastTravelSpeedThresholdMetersPerSecond);
			}
		}
		else
		{
			_fastTravelStoppedElapsedSeconds = 0.0;
		}
		if (next == _fastTravelStreamingActive) return;

		bool resumedAfterStoppedHold = _fastTravelStreamingActive && eligible && !next;
		_fastTravelStreamingActive = next;
		_fastTravelStoppedElapsedSeconds = 0.0;
		_hasLastStreamingCenter = false;
		if (next)
		{
			SetFastTravelChunkVisibility(false);
			_streamingPlanGeneration++;
			_queuedStreamingPlan = null;
			InvalidateAllAsyncVoxelBuilds();
			_pendingChunkLoads.Clear();
			_pendingChunkLoadSet.Clear();
			GD.Print(
				$"Fast-travel voxel streaming suspended at "
				+ $"{actualSpeed:0}m/s actual movement; "
				+ "regional/global terrain remains active.");
		}
		else
		{
			SetFastTravelChunkVisibility(true);
			UpdateStreamingWindow(force: true);
			GD.Print(resumedAfterStoppedHold
				? "Fast-travel voxel streaming resumed at the current canonical position "
					+ $"after {Mathf.Max(0.0f, FastTravelResumeStoppedSeconds):0.00}s stopped."
				: "Fast-travel voxel streaming resumed because noclip fast travel ended.");
		}
	}

	private void SetFastTravelChunkVisibility(bool visible)
	{
		bool hidden = ResolveFastTravelChunkHidden(
			fastTravelActive: !visible,
			HideLocalVoxelMeshesDuringFastTravel);
		if (_fastTravelChunkMeshesHidden == hidden) return;
		_fastTravelChunkMeshesHidden = hidden;
		foreach (VoxelChunkNode chunk in _chunks.Values)
			chunk.Visible = !hidden;
		_voxelVisualCoverageRevision++;
	}

	internal static bool FastTravelChunkVisibilityRegressionTest(out string summary)
	{
		bool defaultRemainsVisible = !ResolveFastTravelChunkHidden(true, false);
		bool legacyValueRemainsVisible = !ResolveFastTravelChunkHidden(true, true);
		bool idleRemainsVisible = !ResolveFastTravelChunkHidden(false, true);
		summary = $"defaultVisible={defaultRemainsVisible}, legacyVisible={legacyValueRemainsVisible}, idleVisible={idleRemainsVisible}";
		return defaultRemainsVisible && legacyValueRemainsVisible && idleRemainsVisible;
	}

	private static bool ResolveFastTravelChunkHidden(
		bool fastTravelActive,
		bool hideLocalVoxelMeshesDuringFastTravel) => false;

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
		{
			return;
		}

		bool isCurvatureHotkey = keyEvent.Keycode == CurvatureWarpIncreaseKey
			|| keyEvent.Keycode == CurvatureWarpDecreaseKey
			|| keyEvent.Keycode == CurvatureWarpResetKey;

		if (!isCurvatureHotkey)
		{
			return;
		}

		if (!EnableCurvatureWarpHotkeys || !UseRingCurvedPlacement || !EnableCurvatureVertexWarp)
		{
			return;
		}

		float currentStrength = CurvatureVertexWarpStrength;
		float step = Mathf.Max(0.001f, CurvatureWarpHotkeyStep);

		if (keyEvent.Keycode == CurvatureWarpIncreaseKey)
		{
			CurvatureVertexWarpStrength = Mathf.Clamp(currentStrength + step, 0.0f, 1.0f);
		}
		else if (keyEvent.Keycode == CurvatureWarpDecreaseKey)
		{
			CurvatureVertexWarpStrength = Mathf.Clamp(currentStrength - step, 0.0f, 1.0f);
		}
		else
		{
			CurvatureVertexWarpStrength = Mathf.Clamp(CurvatureWarpHotkeyResetValue, 0.0f, 1.0f);
		}

		if (Mathf.IsEqualApprox(currentStrength, CurvatureVertexWarpStrength))
		{
			return;
		}

		ApplyCurvatureWarpToLoadedChunksAndRebuild();

		if (LogCurvatureWarpHotkeyChanges)
		{
			GD.Print($"Curvature warp strength: {CurvatureVertexWarpStrength:0.00}");
		}

		GetViewport().SetInputAsHandled();
	}

	public void RegisterFloatingOriginShift(Vector3 worldShift)
	{
		if (!EnableFloatingOriginBridge)
		{
			return;
		}

		_floatingOriginWorldOffset += worldShift;
	}

	public void ResetFloatingOriginBridge()
	{
		_floatingOriginWorldOffset = Vector3.Zero;
		_lastManagerGlobalPosition = GlobalPosition;
		_hasLastManagerGlobalPosition = true;
	}

	public bool RunAroundRingSeamSelfTests()
	{
		if (!RingCoordMath.RunSeamWrapSelfTests())
		{
			return false;
		}

		if (!UseRingCurvedPlacement)
		{
			return true;
		}

		double circumference = GetCanonicalRingCircumferenceMeters64();
		RingChunkCoordinate negativeStreaming = CanonicalPositionToChunkCoordinate(new RingCanonicalPosition(-1.0, 0.0, 0.0));
		RingChunkCoordinate wrappedStreaming = CanonicalPositionToChunkCoordinate(new RingCanonicalPosition(circumference + 17.0, 0.0, 0.0));
		if (negativeStreaming.AroundRingChunk != GetAroundRingChunkLength() - 1
			|| wrappedStreaming.AroundRingChunk != 0)
		{
			return false;
		}

		EnsureTerrainSampler();
		if (_terrainSampler != null
			&& (!_terrainSampler.ValidateElevationContract()
				|| !_terrainSampler.ValidateHydrologyContract()
				|| !_terrainSampler.ValidateBiomeTerrainContract()
				|| !_terrainSampler.ValidateAroundRingSeam(0)
				|| !_terrainSampler.ValidateAroundRingSeam(Mathf.RoundToInt(-RingAcrossBaseOffsetMeters))))
		{
			return false;
		}

		if (UseHabitableRingAxisLayout
			&& EnforceRingAcrossBounds
			&& (GenerateStructuralBedrockEdgeChunks || GenerateStructuralBedrockUndersideChunks))
		{
			int width = Mathf.Max(1, Mathf.RoundToInt(RingAcrossWidthMeters / VoxelConstants.VoxelSizeMeters));
			int hull = VoxelConstants.ChunkSizeZ;
			int bottom = GetStructuralBottomGlobalY();
			int undersideTop = GetStructuralUndersideTopExclusiveGlobalY();
			int top = GetStructuralWallTopGlobalY();
			int interior = width / 2;
			bool invalidWalls = GenerateStructuralBedrockEdgeChunks
				&& (!IsSolidCanonicalVoxel(0, bottom, -1)
					|| !IsSolidCanonicalVoxel(0, top, width)
					|| IsSolidCanonicalVoxel(0, bottom - 1, -1)
					|| IsSolidCanonicalVoxel(0, top + 1, width)
					|| IsSolidCanonicalVoxel(0, 0, -hull - 1)
					|| IsSolidCanonicalVoxel(0, 0, width + hull));
			bool invalidUnderside = GenerateStructuralBedrockUndersideChunks
				&& (!IsSolidCanonicalVoxel(0, bottom, interior)
					|| !IsSolidCanonicalVoxel(0, undersideTop - 1, interior)
					|| IsSolidCanonicalVoxel(0, bottom - 1, interior)
					|| IsSolidCanonicalVoxel(0, undersideTop, interior));
			if (invalidWalls
				|| invalidUnderside
				|| IsSolidCanonicalVoxel(0, MaxBuildGlobalY + 1, interior))
			{
				return false;
			}
		}

		long aroundChunkLength = GetAroundRingChunkLength();
		RingChunkCoordinate seamStart = CanonicalizeChunkCoordinate(new RingChunkCoordinate(0, 0, 0));
		RingChunkCoordinate seamNegativeNeighbor = OffsetChunkCoordinate(seamStart, -1, 0, 0);
		RingChunkCoordinate seamPositiveNeighbor = OffsetChunkCoordinate(
			new RingChunkCoordinate(aroundChunkLength - 1, 0, 0),
			+1,
			0,
			0);

		if (seamNegativeNeighbor.AroundRingChunk != aroundChunkLength - 1)
		{
			return false;
		}

		if (seamPositiveNeighbor.AroundRingChunk != 0)
		{
			return false;
		}

		Transform3D seamTransformA = ChunkCoordinateToLocalTransform(seamStart);
		Transform3D seamTransformB = ChunkCoordinateToLocalTransform(new RingChunkCoordinate(aroundChunkLength, 0, 0));
		if (!seamTransformA.Origin.IsEqualApprox(seamTransformB.Origin))
		{
			return false;
		}

		return true;
	}

	public void RebuildGrid()
	{
		ClearExistingChunks();
		RebuildTerrainNoises();
		InvalidateLodSurfaceEditSnapshot();
		if (DormantEditableBackend)
		{
			// Keep the sampler current for SmoothTerrainGrid and SDF consumers, but
			// never let an inspector/UI rebuild revive the legacy mesh presenter.
			_queuedRebuildDeadlines.Clear();
			return;
		}

		if (EnableChunkStreaming)
		{
			_hasLastStreamingCenter = false;
			_streamingUpdateAccumulator = StreamingUpdateIntervalSeconds;
			UpdateStreamingWindow(force: true);
		}
		else
		{
			BuildStaticChunkWindow();
		}

		_queuedRebuildDeadlines.Clear();
		_seamGapDebugAccumulator = SeamGapDebugUpdateIntervalSeconds;
		OnRebuildGridPhase5();
		OnRebuildGridPhase7();
	}

	public bool TryGetChunk(in RingChunkCoordinate coordinate, out VoxelChunkNode chunk)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
		return _chunks.TryGetValue(canonical, out chunk!);
	}

	/// <summary>
	/// True only when every terrain-surface chunk intersecting the horizontal
	/// region has completed render-mesh installation and remains visible.
	/// </summary>
	public bool IsSurfaceRegionVisualReady(
		double aroundStartMeters,
		double aroundEndMeters,
		double acrossStartMeters,
		double acrossEndMeters)
	{
		if (TerrainSampler == null || _fastTravelChunkMeshesHidden) return false;
		const int aroundSpan = VoxelConstants.ChunkSizeX * VoxelConstants.VoxelSizeMeters;
		const int acrossSpan = VoxelConstants.ChunkSizeZ * VoxelConstants.VoxelSizeMeters;
		double aroundMinimum = Math.Min(aroundStartMeters, aroundEndMeters);
		double aroundMaximum = Math.Max(aroundStartMeters, aroundEndMeters);
		double acrossMinimum = Math.Max(0.0, Math.Min(acrossStartMeters, acrossEndMeters));
		double acrossMaximum = Math.Min(
			RingAcrossWidthMeters,
			Math.Max(acrossStartMeters, acrossEndMeters));
		if (aroundMaximum <= aroundMinimum || acrossMaximum <= acrossMinimum) return false;

		long aroundCell0 = (long)Math.Floor(aroundMinimum / aroundSpan);
		long aroundCell1 = (long)Math.Floor((aroundMaximum - 0.001) / aroundSpan);
		int acrossCell0 = (int)Math.Floor(acrossMinimum / acrossSpan);
		int acrossCell1 = (int)Math.Floor((acrossMaximum - 0.001) / acrossSpan);
		RingLodSurfaceEditSnapshot edits = CaptureLodSurfaceEditSnapshot();
		for (int acrossCell = acrossCell0; acrossCell <= acrossCell1; acrossCell++)
		for (long aroundCell = aroundCell0; aroundCell <= aroundCell1; aroundCell++)
		{
			double around = (aroundCell + 0.5) * aroundSpan;
			int across = Mathf.Clamp(
				Mathf.RoundToInt((float)((acrossCell + 0.5) * acrossSpan)),
				0,
				Mathf.RoundToInt(RingAcrossWidthMeters));
			int surface = edits.GetSurfaceHeight(
				TerrainSampler,
				(long)Math.Round(around),
				across);
			RingChunkCoordinate coordinate = CanonicalPositionToChunkCoordinate(
				new RingCanonicalPosition(around, surface, across));
			if (!_chunks.TryGetValue(coordinate, out VoxelChunkNode? chunk)
				|| !chunk.Visible
				|| !chunk.IsRenderMeshReady)
			{
				return false;
			}
		}
		return true;
	}

	public bool TryGetChunkAtWorldPosition(Vector3 worldPosition, out VoxelChunkNode chunk, out RingChunkCoordinate coordinate)
	{
		coordinate = WorldToChunkCoordinate(worldPosition);
		if (_chunks.TryGetValue(coordinate, out chunk!))
		{
			return true;
		}

		chunk = null!;
		return false;
	}

	public bool IsSolidBlockAtWorldPosition(Vector3 worldPosition)
	{
		if (UseRingCurvedPlacement && UseHabitableRingAxisLayout)
		{
			RingVoxelCoordinate voxel = WorldToCanonicalVoxelCoordinate(worldPosition);
			return IsSolidCanonicalVoxel(voxel.AroundRing, voxel.Vertical, voxel.AcrossRing);
		}

		if (!TryGetChunkAtWorldPosition(worldPosition, out VoxelChunkNode chunk, out RingChunkCoordinate coordinate))
		{
			return false;
		}

		Vector3I localVoxel = chunk.WorldToLocalVoxel(worldPosition);
		if (localVoxel.X < 0 || localVoxel.X >= VoxelConstants.ChunkSizeX
			|| localVoxel.Y < 0 || localVoxel.Y >= VoxelConstants.ChunkSizeY
			|| localVoxel.Z < 0 || localVoxel.Z >= VoxelConstants.ChunkSizeZ)
		{
			return false;
		}

		return (chunk.TryGetRetainedBlock(localVoxel, out BlockType block)
			? block
			: SampleBlockAcrossGrid(coordinate, localVoxel.X, localVoxel.Y, localVoxel.Z)) != BlockType.Air;
	}

	/// <summary>
	/// Finds the first air/solid boundary without requiring a physics body. This
	/// keeps block editing available while streamed collision is being promoted
	/// and avoids using a large-distance float transform for the voxel lookup.
	/// </summary>
	public bool TryRaycastEditableVoxelSurface(
		Vector3 worldOrigin,
		Vector3 worldDirection,
		float maxDistance,
		out Vector3 hitPosition,
		out Vector3 hitNormal)
	{
		hitPosition = Vector3.Zero;
		hitNormal = Vector3.Up;
		float distanceLimit = Mathf.Max(0.0f, maxDistance);
		if (distanceLimit <= 0.0f || worldDirection.IsZeroApprox())
			return false;

		Vector3 direction = worldDirection.Normalized();
		float stepLength = Mathf.Clamp(VoxelConstants.VoxelSizeMeters * 0.25f, 0.05f, 0.5f);
		Vector3 previousPoint = worldOrigin;
		bool previousSolid = IsSolidBlockAtWorldPosition(previousPoint);

		for (float distance = stepLength; distance <= distanceLimit + stepLength * 0.5f; distance += stepLength)
		{
			Vector3 currentPoint = worldOrigin + direction * Mathf.Min(distance, distanceLimit);
			bool currentSolid = IsSolidBlockAtWorldPosition(currentPoint);
			if (currentSolid == previousSolid)
			{
				previousPoint = currentPoint;
				if (distance >= distanceLimit) break;
				continue;
			}

			Vector3 airPoint = previousSolid ? currentPoint : previousPoint;
			Vector3 solidPoint = previousSolid ? previousPoint : currentPoint;
			for (int refinement = 0; refinement < 6; refinement++)
			{
				Vector3 midpoint = (airPoint + solidPoint) * 0.5f;
				if (IsSolidBlockAtWorldPosition(midpoint))
					solidPoint = midpoint;
				else
					airPoint = midpoint;
			}

			hitPosition = (airPoint + solidPoint) * 0.5f;
			hitNormal = (airPoint - solidPoint).Normalized();
			if (hitNormal.IsZeroApprox()) hitNormal = -direction;
			return true;
		}

		return false;
	}

	public bool IsSolidCanonicalVoxel(int aroundVoxel, int verticalVoxel, int acrossVoxel)
		=> IsSolidCanonicalVoxel((long)aroundVoxel, verticalVoxel, acrossVoxel);

	public bool IsSolidCanonicalVoxel(long aroundVoxel, int verticalVoxel, int acrossVoxel)
	{
		if (IsStructuralBedrockVoxel(verticalVoxel, acrossVoxel))
		{
			return true;
		}

		int maximumY = IsAcrossVoxelInStructuralHull(acrossVoxel)
			? GetStructuralWallTopGlobalY()
			: MaxBuildGlobalY;
		if (verticalVoxel < MinExcavationGlobalY || verticalVoxel > maximumY)
		{
			return false;
		}

		var voxelCoordinate = new RingVoxelCoordinate(aroundVoxel, acrossVoxel, verticalVoxel);
		if (!RingCoordMath.TryToLocalVoxel(
			voxelCoordinate,
			GetAroundRingVoxelLength(),
			out RingChunkCoordinate chunkCoordinate,
			out Vector3I localVoxel))
		{
			return false;
		}

		RingChunkCoordinate canonicalChunk = CanonicalizeChunkCoordinate(chunkCoordinate);
		if (_chunks.TryGetValue(canonicalChunk, out VoxelChunkNode? chunk)
			&& chunk.TryGetRetainedBlock(localVoxel, out BlockType retainedBlock))
		{
			return retainedBlock != BlockType.Air;
		}

		// The motor normally queries loaded cells. Falling back to the deterministic
		// baseline keeps collision authoritative during a streaming-window handoff.
		return SampleBlockAcrossGrid(
			canonicalChunk,
			localVoxel.X,
			localVoxel.Y,
			localVoxel.Z) != BlockType.Air;
	}

	/// <summary>Authoritative canonical material lookup for bounded gameplay queries.
	/// It deliberately falls back to the deterministic baseline when a chunk is not streamed.</summary>
	public BlockType GetCanonicalBlockType(long aroundVoxel, int verticalVoxel, int acrossVoxel)
	{
		if (IsStructuralBedrockVoxel(verticalVoxel, acrossVoxel)) return BlockType.Bedrock;
		int maximumY = IsAcrossVoxelInStructuralHull(acrossVoxel) ? GetStructuralWallTopGlobalY() : MaxBuildGlobalY;
		if (verticalVoxel < MinExcavationGlobalY || verticalVoxel > maximumY) return BlockType.Air;
		var voxelCoordinate = new RingVoxelCoordinate(aroundVoxel, acrossVoxel, verticalVoxel);
		if (!RingCoordMath.TryToLocalVoxel(voxelCoordinate, GetAroundRingVoxelLength(), out RingChunkCoordinate chunkCoordinate, out Vector3I localVoxel)) return BlockType.Air;
		RingChunkCoordinate canonicalChunk = CanonicalizeChunkCoordinate(chunkCoordinate);
		if (_chunks.TryGetValue(canonicalChunk, out VoxelChunkNode? chunk)
			&& chunk.TryGetRetainedBlock(localVoxel, out BlockType retainedBlock)) return retainedBlock;
		return SampleBlockAcrossGrid(canonicalChunk, localVoxel.X, localVoxel.Y, localVoxel.Z);
	}

	/// <summary>Authoritative top-of-column query for rain/shelter. Baseline plus every retained non-air override.</summary>
	public bool TryGetHighestRainSurface(long aroundVoxel, int acrossVoxel, int baselineSurface, out int top, out BlockType material)
	{
		top = baselineSurface; material = GetCanonicalBlockType(aroundVoxel, top, acrossVoxel);
		if (acrossVoxel < 0 || acrossVoxel >= RingAcrossWidthMeters) { material = BlockType.Air; return false; }
		long length = GetAroundRingVoxelLength();
		if (length > 0) { aroundVoxel %= length; if (aroundVoxel < 0) aroundVoxel += length; }
		var column = new RingTerrainColumnCoordinate(aroundVoxel, acrossVoxel);
		if (_rainColumnTops.TryGetValue(column, out RainColumnHeights? heights) && heights.Count > 0 && heights.TopHeight > top)
		{ top = heights.TopHeight; material = heights.TopMaterial; }
		return material != BlockType.Air;
	}

	private void RebuildRainColumnTops()
	{
		_rainColumnTops.Clear();
		foreach ((RingChunkCoordinate coordinate, Dictionary<int, ushort> overrides) in _chunkOverrides)
		foreach ((int localIndex, ushort blockId) in overrides)
		{
			if (!VoxelChunkData.TryFromLinearIndex(localIndex, out Vector3I local) || blockId > (ushort)BlockType.Glass || (BlockType)blockId == BlockType.Air) continue;
			RingVoxelCoordinate global = LocalToGlobalVoxel(CanonicalizeChunkCoordinate(coordinate), local);
			var column = new RingTerrainColumnCoordinate(global.AroundRing, global.AcrossRing);
			if (!Enum.IsDefined((BlockType)blockId) || !IsVoxelEditAllowed(GetGeneratedBaselineBlock(CanonicalizeChunkCoordinate(coordinate), local.X, local.Y, local.Z), (BlockType)blockId, global)) continue;
			SetRainColumnOverride(global, (BlockType)blockId);
		}
	}
	private void SetRainColumnOverride(in RingVoxelCoordinate voxel, BlockType material)
	{
		var column = new RingTerrainColumnCoordinate(voxel.AroundRing, voxel.AcrossRing);
		if (material == BlockType.Air) { if (_rainColumnTops.TryGetValue(column, out var old)) { old.Remove(voxel.Vertical); if (old.Count == 0) _rainColumnTops.Remove(column); } return; }
		if (!_rainColumnTops.TryGetValue(column, out var heights)) _rainColumnTops[column] = heights = new RainColumnHeights();
		heights.Set(voxel.Vertical, material);
	}
	private sealed class RainColumnHeights
	{
		private readonly SortedDictionary<int, BlockType> _heights = new();
		public int Count => _heights.Count; public int TopHeight { get; private set; } = int.MinValue; public BlockType TopMaterial { get; private set; } = BlockType.Air;
		public void Set(int height, BlockType material) { _heights[height] = material; if (height >= TopHeight) { TopHeight = height; TopMaterial = material; } }
		public void Remove(int height) { if (!_heights.Remove(height)) return; if (height != TopHeight) return; TopHeight = int.MinValue; TopMaterial = BlockType.Air; foreach ((int y, BlockType material) in _heights) { TopHeight = y; TopMaterial = material; } }
	}

	public Vector3 WorldToCanonicalRingPosition(Vector3 worldPosition)
	{
		RingCanonicalPosition canonical = WorldToCanonicalRingPosition64(worldPosition);
		return new Vector3(
			(float)canonical.AroundMeters,
			(float)canonical.VerticalMeters,
			(float)canonical.AcrossMeters);
	}

	public RingCanonicalPosition WorldToCanonicalRingPosition64(Vector3 worldPosition)
	{
		// Invert the rebased grid transform component-by-component in double.
		// Vector3/Transform3D inversion first reconstructs a five-million-metre
		// float and loses the sub-metre detail that movement and streaming need.
		Transform3D transform = GlobalTransform;
		Basis basis = transform.Basis.Orthonormalized();
		double dx = worldPosition.X + _floatingOriginWorldOffset.X - transform.Origin.X;
		double dy = worldPosition.Y + _floatingOriginWorldOffset.Y - transform.Origin.Y;
		double dz = worldPosition.Z + _floatingOriginWorldOffset.Z - transform.Origin.Z;
		double localX = dx * basis.X.X + dy * basis.X.Y + dz * basis.X.Z;
		double localY = dx * basis.Y.X + dy * basis.Y.Y + dz * basis.Y.Z;
		double localZ = dx * basis.Z.X + dy * basis.Z.Y + dz * basis.Z.Z;

		if (_floatingOriginCanonicalAnchor is { } anchor && UseRingCurvedPlacement && UseHabitableRingAxisLayout)
		{
			RingLodGeometrySnapshot geometry = CaptureLodGeometrySnapshot();
			(double anchorX, double anchorY, double anchorZ) = geometry.CanonicalToAbsoluteLocal(
				anchor.AroundMeters,
				anchor.VerticalMeters,
				anchor.AcrossMeters);
			localX += anchorX;
			localY += anchorY;
			localZ += anchorZ;
		}

		if (!UseRingCurvedPlacement || !UseHabitableRingAxisLayout)
			return new RingCanonicalPosition(localX, localY, localZ);

		double ringX = localX - RingCenterLocalOffset.X;
		double ringY = localY - RingCenterLocalOffset.Y;
		double ringZ = localZ - RingCenterLocalOffset.Z;
		double radius = Math.Max(1.0, GetEffectiveRingRadiusMeters64());
		double angle = Math.Atan2(ringZ, ringX);
		if (angle < 0.0) angle += Math.Tau;
		return new RingCanonicalPosition(
			angle * radius,
			radius - Math.Sqrt(ringX * ringX + ringZ * ringZ) - RingVerticalBaseOffsetMeters,
			ringY - RingAcrossBaseOffsetMeters);
	}

	public Vector3 CanonicalRingPositionToWorld(Vector3 canonicalPosition)
	{
		return CanonicalRingPositionToWorld(new RingCanonicalPosition(
			canonicalPosition.X,
			canonicalPosition.Y,
			canonicalPosition.Z));
	}

	public Vector3 CanonicalRingPositionToWorld(in RingCanonicalPosition canonicalPosition)
	{
		if (!UseRingCurvedPlacement || !UseHabitableRingAxisLayout)
		{
			return ToGlobal(new Vector3(
				(float)canonicalPosition.AroundMeters,
				(float)canonicalPosition.VerticalMeters,
				(float)canonicalPosition.AcrossMeters)) - _floatingOriginWorldOffset;
		}

		return CaptureLodGeometrySnapshot().CanonicalToWorld(
			canonicalPosition.AroundMeters,
			canonicalPosition.VerticalMeters,
			canonicalPosition.AcrossMeters);
	}

	public float GetCanonicalRingCircumferenceMeters()
	{
		return GetAroundRingVoxelLength() * VoxelConstants.VoxelSizeMeters;
	}

	public double GetCanonicalRingCircumferenceMeters64()
	{
		return (double)GetAroundRingChunkLength()
			* VoxelConstants.ChunkSizeX
			* VoxelConstants.VoxelSizeMeters;
	}

	public float GetCanonicalRingRadiusMeters()
	{
		return (float)GetEffectiveRingRadiusMeters64();
	}

	public Vector3 GetCanonicalRingCenterWorld()
	{
		if (!UseRingCurvedPlacement || !UseHabitableRingAxisLayout)
		{
			return GlobalPosition - _floatingOriginWorldOffset;
		}

		double radius = GetEffectiveRingRadiusMeters64();
		return CaptureLodGeometrySnapshot().CanonicalToWorld(
			0.0,
			radius - RingVerticalBaseOffsetMeters,
			-RingAcrossBaseOffsetMeters);
	}

	public int GetStructuralWallTopGlobalY()
	{
		int clearance = Mathf.Max(0, Mathf.RoundToInt(StructuralWallClearanceMeters));
		return checked(MaxBuildGlobalY + clearance);
	}

	public int GetStructuralBottomGlobalY()
	{
		int requestedBottomVoxel = Mathf.FloorToInt(
			-Mathf.Max(1.0f, StructuralDepthMeters)
			/ Mathf.Max(1, VoxelConstants.VoxelSizeMeters));
		return RingCoordMath.FloorDiv(requestedBottomVoxel, VoxelConstants.ChunkSizeY)
			* VoxelConstants.ChunkSizeY;
	}

	public int GetProtectedRoofTopGlobalY()
		=> GetVerticalDomainConfiguration().ProtectedRoofTopGlobalY;

	public int GetLowerHullTopGlobalY()
		=> GetVerticalDomainConfiguration().LowerHullTopGlobalY;

	public RingVerticalDomainConfiguration GetVerticalDomainConfiguration()
		=> new(TerrainBottomGlobalY, ProtectedRoofThicknessMeters, GetStructuralBottomGlobalY(), LowerHullThicknessMeters);

	/// <summary>Returns false instead of silently constructing overlapping terrain/cavity/hull domains.</summary>
	public bool ValidateDeepVerticalDomains(out string error)
	{
		return GetVerticalDomainConfiguration().Validate(out error);
	}

	public int GetStructuralUndersideTopExclusiveGlobalY()
		=> checked(GetStructuralBottomGlobalY() + VoxelConstants.ChunkSizeY);

	public RingLodGeometrySnapshot CaptureLodGeometrySnapshot()
	{
		return new RingLodGeometrySnapshot(
			GlobalTransform,
			_floatingOriginWorldOffset,
			RingCenterLocalOffset,
			GetEffectiveRingRadiusMeters64(),
			RingAcrossBaseOffsetMeters,
			RingVerticalBaseOffsetMeters,
			GetCanonicalRingCircumferenceMeters64(),
			GetStructuralBottomGlobalY() * (double)VoxelConstants.VoxelSizeMeters,
			GetStructuralWallTopGlobalY(),
			_floatingOriginCanonicalAnchor);
	}

	public RingLodSurfaceEditSnapshot CaptureLodSurfaceEditSnapshot()
	{
		if (_cachedLodSurfaceEditSnapshot != null
			&& _cachedLodSurfaceEditSnapshot.Version == _lodSurfaceEditVersion)
		{
			return _cachedLodSurfaceEditSnapshot;
		}

		EnsureTerrainSampler();
		long aroundVoxelLength = GetAroundRingVoxelLength();
		var editedHeights = new Dictionary<RingTerrainColumnCoordinate, int>();
		if (_terrainSampler == null || _chunkOverrides.Count == 0)
		{
			_cachedLodSurfaceEditSnapshot = new RingLodSurfaceEditSnapshot(_lodSurfaceEditVersion, aroundVoxelLength, editedHeights);
			return _cachedLodSurfaceEditSnapshot;
		}

		var columnOverrides = new Dictionary<RingTerrainColumnCoordinate, Dictionary<int, BlockType>>();
		foreach ((RingChunkCoordinate coordinate, Dictionary<int, ushort> overrides) in _chunkOverrides)
		{
			RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
			foreach ((int localIndex, ushort blockId) in overrides)
			{
				if (!VoxelChunkData.TryFromLinearIndex(localIndex, out Vector3I localVoxel))
				{
					continue;
				}

				long around = canonical.AroundRingChunk * VoxelConstants.ChunkSizeX + localVoxel.X;
				int across = canonical.AcrossRingChunk * VoxelConstants.ChunkSizeZ + localVoxel.Z;
				int vertical = canonical.VerticalChunk * VoxelConstants.ChunkSizeY + localVoxel.Y;
				var column = new RingTerrainColumnCoordinate(around, across);
				if (!columnOverrides.TryGetValue(column, out Dictionary<int, BlockType>? verticalOverrides))
				{
					verticalOverrides = new Dictionary<int, BlockType>();
					columnOverrides[column] = verticalOverrides;
				}

				verticalOverrides[vertical] = (BlockType)blockId;
			}
		}

		foreach ((RingTerrainColumnCoordinate column, Dictionary<int, BlockType> verticalOverrides) in columnOverrides)
		{
			int baselineHeight = _terrainSampler.GetSurfaceHeight(column.AroundVoxel, column.AcrossVoxel);
			int candidateHeight = baselineHeight;
			foreach ((int vertical, BlockType blockType) in verticalOverrides)
			{
				if (IsNaturalTerrainBlock(blockType))
				{
					candidateHeight = Mathf.Max(candidateHeight, vertical);
				}
			}

			int resolvedHeight = ResolveEditedColumnSurfaceHeight(
				baselineHeight,
				candidateHeight,
				verticalOverrides,
				_terrainSampler.BedrockY);
			if (resolvedHeight != baselineHeight)
			{
				editedHeights[column] = resolvedHeight;
			}
		}

		_cachedLodSurfaceEditSnapshot = new RingLodSurfaceEditSnapshot(_lodSurfaceEditVersion, aroundVoxelLength, editedHeights);
		return _cachedLodSurfaceEditSnapshot;
	}

	private void UpdateLodSurfaceEditSnapshotForVoxel(
		in RingChunkCoordinate coordinate,
		in Vector3I localVoxel,
		RingLodSurfaceEditSnapshot previousSnapshot,
		int previousSurfaceHeight)
	{
		EnsureTerrainSampler();
		if (_terrainSampler == null)
		{
			InvalidateLodSurfaceEditSnapshot();
			return;
		}

		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
		if (GetAroundRingVoxelLength() > int.MaxValue)
		{
			InvalidateLodSurfaceEditSnapshot();
			return;
		}

		long around = canonical.AroundRingChunk * VoxelConstants.ChunkSizeX + localVoxel.X;
		int across = canonical.AcrossRingChunk * VoxelConstants.ChunkSizeZ + localVoxel.Z;
		int baselineHeight = _terrainSampler.GetSurfaceHeight(around, across);
		int currentSurfaceHeight = ResolveEditedColumnSurfaceHeight(
			checked((int)canonical.AroundRingChunk),
			canonical.AcrossRingChunk,
			localVoxel.X,
			localVoxel.Z,
			baselineHeight);
		if (currentSurfaceHeight == previousSurfaceHeight)
		{
			return;
		}

		var editedHeights = new Dictionary<RingTerrainColumnCoordinate, int>(previousSnapshot.CanonicalEditedColumns);
		var column = new RingTerrainColumnCoordinate(around, across);
		if (currentSurfaceHeight == baselineHeight)
			editedHeights.Remove(column);
		else
			editedHeights[column] = currentSurfaceHeight;

		_lodSurfaceEditVersion++;
		long aroundVoxelLength = GetAroundRingVoxelLength();
		_cachedLodSurfaceEditSnapshot = new RingLodSurfaceEditSnapshot(
			_lodSurfaceEditVersion,
			aroundVoxelLength,
			editedHeights,
			new[] { column });
	}

	private int ResolveEditedColumnSurfaceHeight(
		long aroundChunk,
		int acrossChunk,
		int localX,
		int localZ,
		int baselineHeight)
	{
		var verticalOverrides = new Dictionary<int, BlockType>();
		int candidateHeight = baselineHeight;
		foreach ((RingChunkCoordinate coordinate, Dictionary<int, ushort> overrides) in _chunkOverrides)
		{
			RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
			if (canonical.AroundRingChunk != aroundChunk
				|| canonical.AcrossRingChunk != acrossChunk)
			{
				continue;
			}

			foreach ((int localIndex, ushort blockId) in overrides)
			{
				if (!VoxelChunkData.TryFromLinearIndex(localIndex, out Vector3I voxel)
					|| voxel.X != localX
					|| voxel.Z != localZ)
				{
					continue;
				}

				int vertical = canonical.VerticalChunk * VoxelConstants.ChunkSizeY + voxel.Y;
				BlockType blockType = (BlockType)blockId;
				verticalOverrides[vertical] = blockType;
				if (IsNaturalTerrainBlock(blockType))
					candidateHeight = Mathf.Max(candidateHeight, vertical);
			}
		}

		return ResolveEditedColumnSurfaceHeight(
			baselineHeight,
			candidateHeight,
			verticalOverrides,
			_terrainSampler?.BedrockY ?? baselineHeight);
	}

	private static int ResolveEditedColumnSurfaceHeight(
		int baselineHeight,
		int candidateHeight,
		IReadOnlyDictionary<int, BlockType> verticalOverrides,
		int bedrockY)
	{
		for (int vertical = candidateHeight; vertical >= bedrockY; vertical--)
		{
			bool solid = verticalOverrides.TryGetValue(vertical, out BlockType overrideBlock)
				? IsNaturalTerrainBlock(overrideBlock)
				: vertical <= baselineHeight;
			if (solid)
			{
				return vertical;
			}
		}

		return bedrockY;
	}

	private static bool IsNaturalTerrainBlock(BlockType blockType) =>
		blockType is BlockType.Soil or BlockType.Rock or BlockType.Bedrock;

	private RingTerrainSampleCoordinate CanonicalizeNaturalTerrainSdfSample(long around, int vertical, int across)
	{
		long aroundLength = GetAroundRingChunkLength() * VoxelConstants.ChunkSizeX;
		long canonicalAround = around % aroundLength;
		if (canonicalAround < 0)
			canonicalAround += aroundLength;
		return new RingTerrainSampleCoordinate(canonicalAround, vertical, across);
	}

	private bool TryCarveNaturalTerrainSdf(Vector3 worldPosition)
	{
		if (TerrainSettings == null)
			return false;

		RingTerrainSdfSnapshot? snapshot = CaptureTerrainSdfSnapshot();
		if (snapshot == null)
			return false;

		using (snapshot)
		{
			RingCanonicalPosition canonicalPosition = WorldToCanonicalRingPosition64(worldPosition);
			float radius = Mathf.Max(0.5f, NaturalTerrainSdfBrushRadiusMeters);
			int sampleRadius = Mathf.CeilToInt(radius);
			long centerAround = checked((long)Math.Round(canonicalPosition.AroundMeters));
			int centerVertical = checked((int)Math.Round(canonicalPosition.VerticalMeters));
			int centerAcross = checked((int)Math.Round(canonicalPosition.AcrossMeters));
			bool changed = false;

			for (int across = centerAcross - sampleRadius; across <= centerAcross + sampleRadius; across++)
			for (int vertical = centerVertical - sampleRadius; vertical <= centerVertical + sampleRadius; vertical++)
			for (long around = centerAround - sampleRadius; around <= centerAround + sampleRadius; around++)
			{
				if (across < 0 || across > Mathf.RoundToInt(RingAcrossWidthMeters)
					|| vertical <= TerrainSettings.BedrockY)
				{
					continue;
				}

				double aroundDelta = around - canonicalPosition.AroundMeters;
				double verticalDelta = vertical - canonicalPosition.VerticalMeters;
				double acrossDelta = across - canonicalPosition.AcrossMeters;
				float distance = (float)Math.Sqrt(
					aroundDelta * aroundDelta
					+ verticalDelta * verticalDelta
					+ acrossDelta * acrossDelta);
				if (distance > radius)
					continue;

				RingTerrainSampleCoordinate sample = CanonicalizeNaturalTerrainSdfSample(around, vertical, across);
				float currentDensity = snapshot.SampleDensity(
					sample.AroundVoxel,
					sample.VerticalVoxel,
					sample.AcrossVoxel);
				float desiredDensity = radius - distance + 0.05f;
				if (currentDensity >= desiredDensity)
					continue;

				float existingDelta = _naturalTerrainSdfDeltas.TryGetValue(sample, out float delta)
					? delta
					: 0.0f;
				_naturalTerrainSdfDeltas[sample] = existingDelta + desiredDensity - currentDensity;
				_dirtyNaturalTerrainSdfSamples.Add(sample);
				changed = true;
			}

			if (!changed)
				return false;

			InvalidateLodSurfaceEditSnapshot();
			QueueNaturalTerrainSdfRebuilds(
				centerAround,
				centerVertical,
				centerAcross,
				sampleRadius);
			_hasLastStreamingCenter = false;
			return true;
		}
	}

	private void QueueNaturalTerrainSdfRebuilds(
		long centerAround,
		int centerVertical,
		int centerAcross,
		int sampleRadius)
	{
		int haloBefore = 1;
		int haloAfter = 2;
		long minimumAround = centerAround - sampleRadius - haloAfter;
		long maximumAround = centerAround + sampleRadius + haloBefore;
		int minimumVertical = centerVertical - sampleRadius - haloAfter;
		int maximumVertical = centerVertical + sampleRadius + haloBefore;
		int minimumAcross = centerAcross - sampleRadius - haloAfter;
		int maximumAcross = centerAcross + sampleRadius + haloBefore;

		foreach (RingChunkCoordinate coordinate in _chunks.Keys)
		{
			long chunkAround = coordinate.AroundRingChunk * VoxelConstants.ChunkSizeX;
			int chunkVertical = coordinate.VerticalChunk * VoxelConstants.ChunkSizeY;
			int chunkAcross = coordinate.AcrossRingChunk * VoxelConstants.ChunkSizeZ;
			if (chunkAround > maximumAround
				|| chunkAround + VoxelConstants.ChunkSizeX < minimumAround
				|| chunkVertical > maximumVertical
				|| chunkVertical + VoxelConstants.ChunkSizeY < minimumVertical
				|| chunkAcross > maximumAcross
				|| chunkAcross + VoxelConstants.ChunkSizeZ < minimumAcross)
			{
				continue;
			}
			QueueRebuild(coordinate);
		}
	}

	private void InvalidateLodSurfaceEditSnapshot()
	{
		_lodSurfaceEditVersion++;
		_cachedLodSurfaceEditSnapshot = null;
		_cachedNaturalTerrainSdfEditSnapshot = null;
	}

	public void ApplySurfaceStreamingRadius(int chunkRadius)
	{
		int radius = Mathf.Clamp(chunkRadius, 2, 32);
		StreamingVisualRadiusAround = radius;
		StreamingVisualRadiusAcross = radius;
		StreamingCollisionRadiusAround = Mathf.Min(radius, 2);
		StreamingCollisionRadiusAcross = Mathf.Min(radius, 2);
		_hasLastStreamingCenter = false;
		UpdateStreamingWindow(force: true);
	}

	public void ApplyVerticalStreamingRadius(int chunkRadius)
	{
		int radius = Mathf.Clamp(chunkRadius, 2, 32);
		StreamingVisualRadiusVertical = radius;
		StreamingCollisionRadiusVertical = Mathf.Min(radius, 1);
		_hasLastStreamingCenter = false;
		UpdateStreamingWindow(force: true);
	}

	/// <summary>
	/// Switches the natural-terrain surface representation and remeshes all
	/// resident chunks. Construction blocks continue through the greedy path.
	/// </summary>
	public void SetSmoothNaturalTerrainEnabled(bool enabled)
	{
		if (UseSmoothNaturalTerrainPilot == enabled)
			return;

		UseSmoothNaturalTerrainPilot = enabled;
		InvalidateAllAsyncVoxelBuilds();
		var resident = new List<RingChunkCoordinate>(_chunks.Keys);
		foreach (RingChunkCoordinate coordinate in resident)
		{
			UpdateSmoothTerrainShadowCasting(
				_chunks[coordinate],
				!EnableChunkStreaming || _streamingDesiredCollision.Contains(coordinate));
			if (EnableAsyncVoxelMeshing)
			{
				QueueRebuild(coordinate);
				continue;
			}

			RebuildResidentTerrainModeSynchronously(coordinate);
		}
	}

	public void SetSmoothTerrainNearShadowLimit(bool enabled)
	{
		if (LimitSmoothTerrainShadowsToCollisionRange == enabled)
			return;

		LimitSmoothTerrainShadowsToCollisionRange = enabled;
		foreach ((RingChunkCoordinate coordinate, VoxelChunkNode chunk) in _chunks)
		{
			UpdateSmoothTerrainShadowCasting(
				chunk,
				!EnableChunkStreaming || _streamingDesiredCollision.Contains(coordinate));
		}
	}

	private void RebuildResidentTerrainModeSynchronously(in RingChunkCoordinate coordinate)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
		if (!_chunks.TryGetValue(canonical, out VoxelChunkNode? chunk))
			return;

		NeighborFaceSnapshot neighborFaces = CaptureLoadedNeighborFaces(canonical);
		RingTerrainFineSdfHalo? fineSdfHalo = UseSmoothNaturalTerrainPilot
			&& TerrainSettings?.EnableUnified3dDensityTerrain == true
			? CaptureFineTerrainSdfHalo(canonical)
			: null;
		MeshBuildData mesh = VoxelChunkMesher.Build(
			chunk.Data,
			VoxelBlockPalette.Capture(BlockColorMap),
			neighborFaces.Sample,
			CaptureChunkVertexWarp(canonical),
			buildCollisionFaces: chunk.BuildCollisionOnRebuild,
			smoothNaturalTerrain: UseSmoothNaturalTerrainPilot,
			smoothTerrainHalo: fineSdfHalo);
		chunk.InstallRebuild(mesh);
	}

	/// <summary>
	/// Preloads a canonical destination independently of the camera's current
	/// altitude. Cross-ring flight uses this to keep landing terrain resident
	/// while the craft is still travelling through the empty interior.
	/// </summary>
	public void SetStreamingCanonicalFocusOverride(in RingCanonicalPosition position)
	{
		RingCanonicalPosition wrapped = position.Wrap(GetCanonicalRingCircumferenceMeters64());
		RingChunkCoordinate nextCenter = CanonicalPositionToChunkCoordinate(wrapped);
		if (_streamingCanonicalFocusOverride is { } current
			&& CanonicalPositionToChunkCoordinate(current) == nextCenter)
		{
			_streamingCanonicalFocusOverride = wrapped;
			return;
		}

		_streamingCanonicalFocusOverride = wrapped;
		_hasLastStreamingCenter = false;
		UpdateStreamingWindow(force: true);
	}

	public void ClearStreamingCanonicalFocusOverride()
	{
		if (!_streamingCanonicalFocusOverride.HasValue) return;
		_streamingCanonicalFocusOverride = null;
		_hasLastStreamingCenter = false;
		UpdateStreamingWindow(force: true);
	}

	public bool TrySetBlockByWorldPosition(
		Vector3 worldPosition,
		BlockType blockType,
		out RingChunkCoordinate chunkCoord,
		out Vector3I localVoxel,
		bool requireTargetAir = false)
	{
		chunkCoord = default;
		localVoxel = default;
		if (DormantEditableBackend)
			return false;

		VoxelChunkNode chunk;
		if (UseRingCurvedPlacement && UseHabitableRingAxisLayout)
		{
			RingVoxelCoordinate targetVoxel = WorldToCanonicalVoxelCoordinate(worldPosition);
			if (!RingCoordMath.TryToLocalVoxel(
				targetVoxel,
				GetAroundRingVoxelLength(),
				out chunkCoord,
				out localVoxel))
			{
				return false;
			}

			chunkCoord = CanonicalizeChunkCoordinate(chunkCoord);
			if (!IsChunkWithinVerticalBounds(chunkCoord))
			{
				return false;
			}

			if (!_chunks.TryGetValue(chunkCoord, out chunk!))
				chunk = EnsureChunkLoaded(chunkCoord, _streamingDesiredCollision.Contains(chunkCoord));
		}
		else
		{
			if (!TryGetChunkAtWorldPosition(worldPosition, out chunk, out chunkCoord))
			{
				chunkCoord = WorldToChunkCoordinate(worldPosition);
				if (!IsChunkWithinVerticalBounds(chunkCoord))
				{
					return false;
				}

				chunk = EnsureChunkLoaded(chunkCoord, _streamingDesiredCollision.Contains(chunkCoord));
			}

			localVoxel = chunk.WorldToLocalVoxel(worldPosition);
		}

		if (!chunk.Data.IsInBounds(localVoxel.X, localVoxel.Y, localVoxel.Z))
		{
			return false;
		}

		if (UseSmoothNaturalTerrainPilot
			&& blockType == BlockType.Air
			&& IsNaturalTerrainBlock(chunk.Data.GetBlock(localVoxel.X, localVoxel.Y, localVoxel.Z)))
		{
			return TryCarveNaturalTerrainSdf(worldPosition);
		}

		if (EnableServerAuthoritativeEditPath)
		{
			RingChunkCoordinate canonicalChunk = CanonicalizeChunkCoordinate(chunkCoord);
			RingVoxelCoordinate targetVoxel = RingCoordMath.ToVoxelCoordinate(
				canonicalChunk,
				localVoxel,
				GetAroundRingVoxelLength());

			var command = new VoxelBlockEditCommand(
				CommandId: (long)Time.GetTicksMsec(),
				AuthorityId: ResolveLocalCommandAuthorityId(),
				TargetVoxel: targetVoxel,
				BlockType: blockType,
				RequireTargetAir: requireTargetAir);

			bool applied = TryApplyAuthoritativeEditCommand(command, out VoxelBlockEditResult result);
			if (!applied)
			{
				return false;
			}

			chunkCoord = result.ChunkCoordinate;
			localVoxel = result.LocalVoxel;
			return true;
		}

		BlockType current = chunk.Data.GetBlock(localVoxel.X, localVoxel.Y, localVoxel.Z);
		if (requireTargetAir && current != BlockType.Air)
		{
			return false;
		}

		RingVoxelCoordinate globalVoxel = LocalToGlobalVoxel(chunkCoord, localVoxel);
		if (!IsVoxelEditAllowed(current, blockType, globalVoxel))
		{
			return false;
		}

		bool didSet = chunk.TrySetBlock(localVoxel, blockType, rebuild: false);
		if (!didSet)
		{
			return false;
		}

		UpdateOverrideForVoxel(chunkCoord, localVoxel, chunk);
		RebuildChunkAndNeighbors(chunkCoord, localVoxel);
		OnVoxelEditedPhase5(chunkCoord, localVoxel, blockType);
		return true;
	}

	public void RebuildChunkAndNeighbors(in RingChunkCoordinate centerCoord, Vector3I localVoxel)
	{
		RingChunkCoordinate canonicalCenter = CanonicalizeChunkCoordinate(centerCoord);
		QueueRebuild(canonicalCenter);
		bool invalidatedVirtualNeighbor = false;

		if (localVoxel.X == 0)
		{
			RingChunkCoordinate neighbor = OffsetChunkCoordinate(canonicalCenter, -1, 0, 0);
			InvalidateVirtualChunkClassification(neighbor);
			QueueRebuild(neighbor);
			invalidatedVirtualNeighbor = true;
		}
		else if (localVoxel.X == VoxelConstants.ChunkSizeX - 1)
		{
			RingChunkCoordinate neighbor = OffsetChunkCoordinate(canonicalCenter, +1, 0, 0);
			InvalidateVirtualChunkClassification(neighbor);
			QueueRebuild(neighbor);
			invalidatedVirtualNeighbor = true;
		}

		if (localVoxel.Z == 0)
		{
			RingChunkCoordinate neighbor = OffsetChunkCoordinate(canonicalCenter, 0, -1, 0);
			InvalidateVirtualChunkClassification(neighbor);
			QueueRebuild(neighbor);
			invalidatedVirtualNeighbor = true;
		}
		else if (localVoxel.Z == VoxelConstants.ChunkSizeZ - 1)
		{
			RingChunkCoordinate neighbor = OffsetChunkCoordinate(canonicalCenter, 0, +1, 0);
			InvalidateVirtualChunkClassification(neighbor);
			QueueRebuild(neighbor);
			invalidatedVirtualNeighbor = true;
		}

		if (localVoxel.Y == 0)
		{
			RingChunkCoordinate neighbor = OffsetChunkCoordinate(canonicalCenter, 0, 0, -1);
			InvalidateVirtualChunkClassification(neighbor);
			QueueRebuild(neighbor);
			invalidatedVirtualNeighbor = true;
		}
		else if (localVoxel.Y == VoxelConstants.ChunkSizeY - 1)
		{
			RingChunkCoordinate neighbor = OffsetChunkCoordinate(canonicalCenter, 0, 0, +1);
			InvalidateVirtualChunkClassification(neighbor);
			QueueRebuild(neighbor);
			invalidatedVirtualNeighbor = true;
		}
		if (invalidatedVirtualNeighbor)
			_hasLastStreamingCenter = false;
	}

	public void FlushRebuildQueue()
	{
		if (_queuedRebuildDeadlines.Count == 0)
		{
			return;
		}

		var coords = new List<RingChunkCoordinate>(_queuedRebuildDeadlines.Keys);
		_queuedRebuildDeadlines.Clear();

		foreach (RingChunkCoordinate coord in coords)
		{
			RebuildIfExistsImmediate(coord);
		}
	}

	private void BuildStaticChunkWindow()
	{
		if (DormantEditableBackend)
			return;

		int radiusAround = Mathf.Max(0, ChunkRadiusAround);
		int radiusAcross = Mathf.Max(0, ChunkRadiusAcross);
		int radiusVertical = Mathf.Max(0, ChunkRadiusVertical);

		for (int vertical = -radiusVertical; vertical <= radiusVertical; vertical++)
		{
			for (int across = -radiusAcross; across <= radiusAcross; across++)
			{
				for (int around = -radiusAround; around <= radiusAround; around++)
				{
					EnsureChunkLoaded(new RingChunkCoordinate(around, across, vertical));
				}
			}
		}

		foreach (VoxelChunkNode chunk in _chunks.Values)
		{
			chunk.SetCollisionEnabled(true);
		}

		FlushRebuildQueue();
	}

	private void UpdateStreamingWindow(bool force)
	{
		if (DormantEditableBackend)
			return;

		if (!EnableChunkStreaming)
		{
			return;
		}

		if (!TryGetStreamingCanonicalFocus(out RingCanonicalPosition canonicalFocus))
		{
			return;
		}

		RingChunkCoordinate center = CanonicalPositionToChunkCoordinate(canonicalFocus);
		Vector3 predictionOffset = GetPredictiveCanonicalOffset();
		RingChunkCoordinate predictedCenter = CanonicalPositionToChunkCoordinate(canonicalFocus.AddLocal(predictionOffset));
		ResolveAdaptiveStreamingVerticalRadii(
			canonicalFocus,
			out int visualVerticalRadius,
			out int collisionVerticalRadius);
		// Altitude-aware streaming previously only reduced the vertical radius.
		// It therefore retained a full around/across window at the player's
		// location even in empty space. A canonical override is intentionally
		// exempt: cross-ring flight uses it to preload a surface destination.
		if (ShouldSuspendAutomaticSurfaceStreaming(
			EnableAltitudeAwareStreamingRadii,
			_surfaceResidencyActive,
			_fastTravelStreamingActive,
			_streamingCanonicalFocusOverride.HasValue))
		{
			SuspendAutomaticSurfaceStreaming();
			return;
		}
		_automaticSurfaceStreamingSuspended = false;
		if (!force
			&& _hasLastStreamingCenter
			&& center == _lastStreamingCenter
			&& predictedCenter == _lastPredictedStreamingCenter
			&& visualVerticalRadius == _lastStreamingVisualVerticalRadius
			&& collisionVerticalRadius == _lastStreamingCollisionVerticalRadius)
		{
			return;
		}

		_lastStreamingCenter = center;
		_lastPredictedStreamingCenter = predictedCenter;
		_lastStreamingVisualVerticalRadius = visualVerticalRadius;
		_lastStreamingCollisionVerticalRadius = collisionVerticalRadius;
		_hasLastStreamingCenter = true;
		if (EnableAsyncStreamingPlanning)
		{
			ScheduleStreamingWindowPlan(
				canonicalFocus,
				center,
				predictedCenter,
				predictionOffset,
				visualVerticalRadius,
				collisionVerticalRadius);
			return;
		}

		HashSet<RingChunkCoordinate> desiredVisual = BuildChunkSetAroundCenter(
			center,
			Mathf.Max(0, StreamingVisualRadiusAround),
			Mathf.Max(0, StreamingVisualRadiusAcross),
			visualVerticalRadius);

		HashSet<RingChunkCoordinate> desiredCollision = BuildChunkSetAroundCenter(
			center,
			Mathf.Max(0, StreamingCollisionRadiusAround),
			Mathf.Max(0, StreamingCollisionRadiusAcross),
			collisionVerticalRadius);

		if (predictionOffset.LengthSquared() >= 1.0f)
		{
			AddPredictiveChunkCorridor(desiredVisual, canonicalFocus, predictionOffset,
				Mathf.Max(0, StreamingVisualRadiusAround),
				Mathf.Max(0, StreamingVisualRadiusAcross),
				visualVerticalRadius);
			AddPredictiveChunkCorridor(desiredCollision, canonicalFocus, predictionOffset,
				Mathf.Max(0, StreamingCollisionRadiusAround),
				Mathf.Max(0, StreamingCollisionRadiusAcross),
				collisionVerticalRadius);
		}

		_streamingDesiredVisual = desiredVisual;
		_streamingDesiredCollision = desiredCollision;

		if (UseIncrementalChunkLoading)
		{
			RebuildPrioritizedChunkLoadQueue(desiredVisual, desiredCollision, center, predictedCenter);
		}
		else
		{
			foreach (RingChunkCoordinate coord in desiredVisual)
			{
				bool requiresSolidCollision = desiredCollision.Contains(coord);
				if (!_chunks.ContainsKey(coord)
					&& !IsAsyncVoxelBuildPending(coord)
					&& !_knownEmptyStreamingChunks.Contains(coord)
					&& (!_knownSolidStreamingChunks.Contains(coord) || requiresSolidCollision))
					LoadStreamingChunkIfNeeded(coord, desiredCollision.Contains(coord));
			}
		}

		var toUnload = new List<RingChunkCoordinate>();
		foreach ((RingChunkCoordinate coord, VoxelChunkNode _) in _chunks)
		{
			if (!desiredVisual.Contains(coord))
			{
				toUnload.Add(coord);
			}
		}

		foreach (RingChunkCoordinate coord in toUnload)
		{
			UnloadChunk(coord);
		}

		_knownEmptyStreamingChunks.RemoveWhere(coord => !desiredVisual.Contains(coord));
		_knownSolidStreamingChunks.RemoveWhere(coord => !desiredVisual.Contains(coord));

		foreach ((RingChunkCoordinate coord, VoxelChunkNode chunk) in _chunks)
		{
			bool collisionEnabled = desiredCollision.Contains(coord);
			UpdateLoadedChunkResidency(coord, chunk, collisionEnabled);
		}

		if (UseIncrementalChunkLoading)
		{
			ProcessIncrementalChunkLoads();
		}
	}

	private static bool ShouldSuspendAutomaticSurfaceStreaming(
		bool altitudeAware,
		bool surfaceResidencyActive,
		bool fastTravelActive,
		bool hasCanonicalFocusOverride) => altitudeAware
		&& !surfaceResidencyActive
		&& !fastTravelActive
		&& !hasCanonicalFocusOverride;

	private static bool RunStreamingResidencySelfTest(out string summary)
	{
		bool spaceUnloads = ShouldSuspendAutomaticSurfaceStreaming(
			altitudeAware: true,
			surfaceResidencyActive: false,
			fastTravelActive: false,
			hasCanonicalFocusOverride: false);
		bool surfaceStaysResident = !ShouldSuspendAutomaticSurfaceStreaming(
			altitudeAware: true,
			surfaceResidencyActive: true,
			fastTravelActive: false,
			hasCanonicalFocusOverride: false);
		bool landingPreloadStaysResident = !ShouldSuspendAutomaticSurfaceStreaming(
			altitudeAware: true,
			surfaceResidencyActive: false,
			fastTravelActive: false,
			hasCanonicalFocusOverride: true);
		bool fastTravelDoesNotEvictCache = !ShouldSuspendAutomaticSurfaceStreaming(
			altitudeAware: true,
			surfaceResidencyActive: false,
			fastTravelActive: true,
			hasCanonicalFocusOverride: false);
		bool disabledDoesNotChangeExistingPolicy = !ShouldSuspendAutomaticSurfaceStreaming(
			altitudeAware: false,
			surfaceResidencyActive: false,
			fastTravelActive: false,
			hasCanonicalFocusOverride: false);
		bool passed = spaceUnloads && surfaceStaysResident
			&& landingPreloadStaysResident && fastTravelDoesNotEvictCache
			&& disabledDoesNotChangeExistingPolicy;
		summary = $"spaceUnload={spaceUnloads}, surface={surfaceStaysResident}, landingOverride={landingPreloadStaysResident}, fastTravel={fastTravelDoesNotEvictCache}, disabled={disabledDoesNotChangeExistingPolicy}";
		return passed;
	}

	private void SuspendAutomaticSurfaceStreaming()
	{
		if (_automaticSurfaceStreamingSuspended)
			return;
		_automaticSurfaceStreamingSuspended = true;
		_streamingPlanGeneration++;
		_queuedStreamingPlan = null;
		_pendingChunkLoads.Clear();
		_pendingChunkLoadSet.Clear();
		_streamingDesiredVisual.Clear();
		_streamingDesiredCollision.Clear();
		_hasLastStreamingCenter = false;

		if (_chunks.Count > 0)
		{
			var unload = new List<RingChunkCoordinate>(_chunks.Keys);
			foreach (RingChunkCoordinate coordinate in unload)
				UnloadChunk(coordinate);
		}

		// These sets are merely streaming classifications. Persistent edits live
		// in _chunkOverrides/journals and are never discarded by residency.
		_knownEmptyStreamingChunks.Clear();
		_knownSolidStreamingChunks.Clear();
		_voxelVisualCoverageRevision++;
	}

	private void ResolveAdaptiveStreamingVerticalRadii(
		in RingCanonicalPosition canonicalFocus,
		out int visualRadius,
		out int collisionRadius)
	{
		visualRadius = Mathf.Max(0, StreamingVisualRadiusVertical);
		collisionRadius = Mathf.Max(0, StreamingCollisionRadiusVertical);
		if (!EnableAltitudeAwareStreamingRadii || _fastTravelStreamingActive)
		{
			_surfaceResidencyActive = false;
			return;
		}

		EnsureTerrainSampler();
		int across = Mathf.Clamp(
			Mathf.RoundToInt((float)canonicalFocus.AcrossMeters),
			0,
			Mathf.Max(0, Mathf.RoundToInt(RingAcrossWidthMeters) - 1));
		int surface = _terrainSampler?.GetSurfaceHeight(
			(long)Math.Round(canonicalFocus.AroundMeters),
			across) ?? 0;
		double altitudeAboveSurface = canonicalFocus.VerticalMeters - surface;
		float enterAltitude = Mathf.Max(1.0f, SurfaceResidencyEnterAltitudeMeters);
		float exitAltitude = Mathf.Max(enterAltitude, SurfaceResidencyExitAltitudeMeters);
		if (_surfaceResidencyActive)
			_surfaceResidencyActive = altitudeAboveSurface <= exitAltitude;
		else
			_surfaceResidencyActive = altitudeAboveSurface <= enterAltitude;
		if (!_surfaceResidencyActive) return;

		visualRadius = Mathf.Min(
			visualRadius,
			Mathf.Max(0, SurfaceVisualRadiusVertical));
		collisionRadius = Mathf.Min(
			collisionRadius,
			Mathf.Max(0, SurfaceCollisionRadiusVertical));
	}

	private void UpdateLoadedChunkResidency(
		in RingChunkCoordinate coordinate,
		VoxelChunkNode chunk,
		bool collisionEnabled)
	{
		bool collisionReady = chunk.SetCollisionEnabled(collisionEnabled);
		if (collisionEnabled && !collisionReady)
		{
			// The visual mesh intentionally does not retain a duplicate CPU vertex
			// buffer. Promote it through the existing async resident-remesh path.
			QueueRebuild(coordinate);
		}
		UpdateSmoothTerrainShadowCasting(chunk, collisionEnabled);
		if (!collisionEnabled)
			TryReleaseUneditedVisualChunkData(coordinate, chunk);
	}

	private void TryReleaseUneditedVisualChunkData(
		in RingChunkCoordinate coordinate,
		VoxelChunkNode chunk)
	{
		if (!ReleaseUneditedVisualChunkData
			|| !UseDeterministicTerrainGeneration
			|| TerrainSettings == null
			|| _chunkOverrides.ContainsKey(CanonicalizeChunkCoordinate(coordinate)))
		{
			return;
		}
		chunk.ReleaseVoxelData();
	}

	private void ScheduleStreamingWindowPlan(
		in RingCanonicalPosition canonicalFocus,
		in RingChunkCoordinate center,
		in RingChunkCoordinate predictedCenter,
		Vector3 predictionOffset,
		int visualVerticalRadius,
		int collisionVerticalRadius)
	{
		var pending = new HashSet<RingChunkCoordinate>(
			_pendingVoxelBuildEpochs.Keys);
		pending.UnionWith(_deferredVoxelBuildSet);
		var request = new StreamingWindowRequest(
			canonicalFocus,
			center,
			predictedCenter,
			predictionOffset,
			new HashSet<RingChunkCoordinate>(_chunks.Keys),
			pending,
			new HashSet<RingChunkCoordinate>(_knownEmptyStreamingChunks),
			new HashSet<RingChunkCoordinate>(_knownSolidStreamingChunks),
			Mathf.Max(0, StreamingVisualRadiusAround),
			Mathf.Max(0, StreamingVisualRadiusAcross),
			visualVerticalRadius,
			Mathf.Max(0, StreamingCollisionRadiusAround),
			Mathf.Max(0, StreamingCollisionRadiusAcross),
			collisionVerticalRadius,
			++_streamingPlanGeneration);
		if (_streamingPlanTask != null)
		{
			_queuedStreamingPlan = request;
			return;
		}
		StartStreamingWindowPlan(request);
	}

	private void StartStreamingWindowPlan(StreamingWindowRequest request)
	{
		_streamingPlanTask = Task.Run(() =>
			BuildStreamingWindowPlan(request));
	}

	private StreamingWindowPlan BuildStreamingWindowPlan(
		StreamingWindowRequest request)
	{
		HashSet<RingChunkCoordinate> desiredVisual =
			BuildChunkSetAroundCenter(
				request.Center,
				request.VisualRadiusAround,
				request.VisualRadiusAcross,
				request.VisualRadiusVertical);
		HashSet<RingChunkCoordinate> desiredCollision =
			BuildChunkSetAroundCenter(
				request.Center,
				request.CollisionRadiusAround,
				request.CollisionRadiusAcross,
				request.CollisionRadiusVertical);
		if (request.PredictionOffset.LengthSquared() >= 1.0f)
		{
			AddPredictiveChunkCorridor(
				desiredVisual,
				request.CanonicalFocus,
				request.PredictionOffset,
				request.VisualRadiusAround,
				request.VisualRadiusAcross,
				request.VisualRadiusVertical);
			AddPredictiveChunkCorridor(
				desiredCollision,
				request.CanonicalFocus,
				request.PredictionOffset,
				request.CollisionRadiusAround,
				request.CollisionRadiusAcross,
				request.CollisionRadiusVertical);
		}

		var missing = new List<RingChunkCoordinate>();
		foreach (RingChunkCoordinate coordinate in desiredVisual)
		{
			RingChunkCoordinate canonical =
				CanonicalizeChunkCoordinate(coordinate);
			bool requiresCollision = desiredCollision.Contains(canonical);
			if (!request.Loaded.Contains(canonical)
				&& !request.Pending.Contains(canonical)
				&& !request.KnownEmpty.Contains(canonical)
				&& (!request.KnownSolid.Contains(canonical)
					|| requiresCollision))
			{
				missing.Add(canonical);
			}
		}
		RingChunkCoordinate currentCenter = request.Center;
		RingChunkCoordinate predictedCenter = request.PredictedCenter;
		missing.Sort((a, b) =>
		{
			bool aCollision = desiredCollision.Contains(a);
			bool bCollision = desiredCollision.Contains(b);
			if (aCollision != bCollision) return aCollision ? -1 : 1;
			int aCurrent = ChunkManhattanDistance(a, currentCenter);
			int bCurrent = ChunkManhattanDistance(b, currentCenter);
			int aPredicted = ChunkManhattanDistance(a, predictedCenter);
			int bPredicted = ChunkManhattanDistance(b, predictedCenter);
			int aScore = Mathf.Min(aCurrent * 2, aPredicted * 2 + 1);
			int bScore = Mathf.Min(bCurrent * 2, bPredicted * 2 + 1);
			return aScore != bScore
				? aScore.CompareTo(bScore)
				: aCurrent.CompareTo(bCurrent);
		});

		var departed = new List<RingChunkCoordinate>();
		foreach (RingChunkCoordinate loaded in request.Loaded)
		{
			if (!desiredVisual.Contains(loaded))
				departed.Add(loaded);
		}
		return new StreamingWindowPlan(
			desiredVisual,
			desiredCollision,
			missing.ToArray(),
			departed.ToArray(),
			request.Generation);
	}

	private void ServiceCompletedStreamingPlan()
	{
		if (DormantEditableBackend)
			return;

		if (_streamingPlanTask == null || !_streamingPlanTask.IsCompleted)
			return;
		Task<StreamingWindowPlan> completed = _streamingPlanTask;
		_streamingPlanTask = null;
		if (completed.IsCompletedSuccessfully)
		{
			StreamingWindowPlan plan = completed.Result;
			if (plan.Generation == _streamingPlanGeneration
				&& (!_fastTravelStreamingActive
					|| _streamingCanonicalFocusOverride.HasValue))
			{
				ApplyStreamingWindowPlan(plan);
			}
		}
		else if (completed.IsFaulted)
		{
			GD.PushError(
				$"Voxel streaming planner failed: "
				+ $"{completed.Exception?.GetBaseException()}");
			_hasLastStreamingCenter = false;
		}

		if (_queuedStreamingPlan is { } queued)
		{
			_queuedStreamingPlan = null;
			StartStreamingWindowPlan(queued);
		}
	}

	private void ApplyStreamingWindowPlan(StreamingWindowPlan plan)
	{
		if (DormantEditableBackend)
			return;

		_streamingDesiredVisual = plan.DesiredVisual;
		_streamingDesiredCollision = plan.DesiredCollision;
		_pendingChunkLoads.Clear();
		_pendingChunkLoadSet.Clear();
		foreach (RingChunkCoordinate coordinate in plan.PrioritizedMissing)
		{
			bool requiresCollision =
				_streamingDesiredCollision.Contains(coordinate);
			if (!_streamingDesiredVisual.Contains(coordinate)
				|| _chunks.ContainsKey(coordinate)
				|| IsAsyncVoxelBuildPending(coordinate)
				|| _knownEmptyStreamingChunks.Contains(coordinate)
				|| (_knownSolidStreamingChunks.Contains(coordinate)
					&& !requiresCollision))
			{
				continue;
			}
			if (_pendingChunkLoadSet.Add(coordinate))
				_pendingChunkLoads.Enqueue(coordinate);
		}

		foreach (RingChunkCoordinate coordinate in plan.Departed)
		{
			if (_chunks.ContainsKey(coordinate)
				&& !_streamingDesiredVisual.Contains(coordinate))
			{
				UnloadChunk(coordinate);
			}
		}
		_knownEmptyStreamingChunks.RemoveWhere(
			coordinate => !_streamingDesiredVisual.Contains(coordinate));
		_knownSolidStreamingChunks.RemoveWhere(
			coordinate => !_streamingDesiredVisual.Contains(coordinate));
		foreach ((RingChunkCoordinate coordinate, VoxelChunkNode chunk)
				 in _chunks)
		{
			bool collisionEnabled = _streamingDesiredCollision.Contains(coordinate);
			UpdateLoadedChunkResidency(coordinate, chunk, collisionEnabled);
		}

		if (UseIncrementalChunkLoading)
		{
			ProcessIncrementalChunkLoads();
			return;
		}
		foreach (RingChunkCoordinate coordinate
				 in _streamingDesiredVisual)
		{
			bool requiresCollision =
				_streamingDesiredCollision.Contains(coordinate);
			if (!_chunks.ContainsKey(coordinate)
				&& !IsAsyncVoxelBuildPending(coordinate)
				&& !_knownEmptyStreamingChunks.Contains(coordinate)
				&& (!_knownSolidStreamingChunks.Contains(coordinate)
					|| requiresCollision))
			{
				LoadStreamingChunkIfNeeded(
					coordinate,
					requiresCollision);
			}
		}
	}

	private void RebuildPrioritizedChunkLoadQueue(
		HashSet<RingChunkCoordinate> desiredVisual,
		HashSet<RingChunkCoordinate> desiredCollision,
		in RingChunkCoordinate currentCenter,
		in RingChunkCoordinate predictedCenter)
	{
		_pendingChunkLoads.Clear();
		_pendingChunkLoadSet.Clear();

		var missing = new List<RingChunkCoordinate>();
		foreach (RingChunkCoordinate coordinate in desiredVisual)
		{
			RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
			bool requiresSolidCollision = desiredCollision.Contains(canonical);
			if (!_chunks.ContainsKey(canonical)
				&& !IsAsyncVoxelBuildPending(canonical)
				&& !_knownEmptyStreamingChunks.Contains(canonical)
				&& (!_knownSolidStreamingChunks.Contains(canonical) || requiresSolidCollision))
				missing.Add(canonical);
		}

		RingChunkCoordinate currentCenterValue = currentCenter;
		RingChunkCoordinate predictedCenterValue = predictedCenter;
		missing.Sort((a, b) =>
		{
			bool aCollision = desiredCollision.Contains(a);
			bool bCollision = desiredCollision.Contains(b);
			if (aCollision != bCollision) return aCollision ? -1 : 1;
			int aCurrent = ChunkManhattanDistance(a, currentCenterValue);
			int bCurrent = ChunkManhattanDistance(b, currentCenterValue);
			int aPredicted = ChunkManhattanDistance(a, predictedCenterValue);
			int bPredicted = ChunkManhattanDistance(b, predictedCenterValue);
			int aScore = Mathf.Min(aCurrent * 2, aPredicted * 2 + 1);
			int bScore = Mathf.Min(bCurrent * 2, bPredicted * 2 + 1);
			return aScore != bScore ? aScore.CompareTo(bScore) : aCurrent.CompareTo(bCurrent);
		});

		foreach (RingChunkCoordinate coordinate in missing)
		{
			if (_pendingChunkLoadSet.Add(coordinate)) _pendingChunkLoads.Enqueue(coordinate);
		}
	}

	private int ChunkManhattanDistance(in RingChunkCoordinate a, in RingChunkCoordinate b)
	{
		long aroundDifference = Math.Abs(a.AroundRingChunk - b.AroundRingChunk);
		long aroundLength = GetAroundRingChunkLength();
		if (aroundLength > 0) aroundDifference = Math.Min(aroundDifference, aroundLength - aroundDifference);
		return checked((int)aroundDifference)
			+ Mathf.Abs(a.AcrossRingChunk - b.AcrossRingChunk)
			+ Mathf.Abs(a.VerticalChunk - b.VerticalChunk);
	}

	private void ProcessIncrementalChunkLoads()
	{
		if (!EnableChunkStreaming
			|| !UseIncrementalChunkLoading
			|| (_pendingChunkLoads.Count == 0 && DeferredVoxelMeshCount == 0))
		{
			return;
		}

		int loadBudget = Mathf.Max(1, MaxChunkLoadsPerFrame);
		int classificationBudget = Mathf.Max(loadBudget, MaxChunkClassificationsPerFrame);
		ulong startUsec = Time.GetTicksUsec();
		double timeBudgetUsec = Math.Max(0.25, MaxChunkStreamingMillisecondsPerFrame) * 1000.0;
		int loaded = ScheduleDeferredAsyncVoxelBuilds(loadBudget);
		int classified = 0;
		while (loaded < loadBudget
			&& classified < classificationBudget
			&& _pendingChunkLoads.Count > 0)
		{
			if (classified > 0 && Time.GetTicksUsec() - startUsec >= timeBudgetUsec)
				break;

			RingChunkCoordinate coordinate = _pendingChunkLoads.Dequeue();
			_pendingChunkLoadSet.Remove(coordinate);
			bool requiresSolidCollision = _streamingDesiredCollision.Contains(coordinate);
			if (!_streamingDesiredVisual.Contains(coordinate)
				|| _chunks.ContainsKey(coordinate)
				|| IsAsyncVoxelBuildPending(coordinate)
				|| _knownEmptyStreamingChunks.Contains(coordinate)
				|| (_knownSolidStreamingChunks.Contains(coordinate) && !requiresSolidCollision))
			{
				continue;
			}

			classified++;
			if (LoadStreamingChunkIfNeeded(coordinate, requiresSolidCollision))
				loaded++;
		}
	}

	private bool TryGetStreamingFocusPosition(out Vector3 focusPosition)
	{
		focusPosition = Vector3.Zero;

		ResolveStreamingFocusNode();
		if (_streamingFocusNode != null)
		{
			focusPosition = _streamingFocusNode.GlobalPosition;
			return true;
		}

		Camera3D? camera = GetViewport().GetCamera3D();
		if (camera != null)
		{
			focusPosition = camera.GlobalPosition;
			return true;
		}

		return false;
	}

	private bool TryGetStreamingCanonicalFocus(out RingCanonicalPosition canonicalPosition)
	{
		if (_streamingCanonicalFocusOverride is { } focusOverride)
		{
			canonicalPosition = focusOverride;
			return true;
		}

		ResolveStreamingVelocityProvider();
		if (_streamingVelocityProvider != null)
		{
			canonicalPosition = _streamingVelocityProvider.CanonicalPosition;
			return true;
		}

		if (TryGetStreamingFocusPosition(out Vector3 focusPosition))
		{
			canonicalPosition = WorldToCanonicalRingPosition64(focusPosition);
			return true;
		}

		canonicalPosition = default;
		return false;
	}

	private Vector3 GetPredictiveCanonicalOffset()
	{
		LastPredictionDistanceMeters = 0.0f;
		LastBrakingDistanceMeters = 0.0f;
		if (_streamingCanonicalFocusOverride.HasValue
			|| !EnablePredictiveStreaming
			|| PredictionLookAheadSeconds <= 0.0f
			|| PredictionMaxDistanceMeters <= 0.0f)
		{
			return Vector3.Zero;
		}

		ResolveStreamingVelocityProvider();
		if (_streamingVelocityProvider == null)
		{
			return Vector3.Zero;
		}

		Vector3 velocity = _streamingVelocityProvider.CanonicalVelocity;
		if (!velocity.IsFinite()) return Vector3.Zero;
		float speed = velocity.Length();
		Vector3 predictedOffset = velocity * PredictionLookAheadSeconds;
		if (IncludeBrakingDistanceInPrediction && speed > 0.01f)
		{
			float brakingAcceleration = Mathf.Max(1.0f, PredictionBrakingAcceleration);
			LastBrakingDistanceMeters = speed * speed / (2.0f * brakingAcceleration)
				* Mathf.Max(1.0f, PredictionBrakingSafetyFactor);
			predictedOffset += velocity / speed * LastBrakingDistanceMeters;
		}

		predictedOffset = predictedOffset.LimitLength(PredictionMaxDistanceMeters);
		LastPredictionDistanceMeters = predictedOffset.Length();
		return predictedOffset;
	}

	private void AddPredictiveChunkCorridor(
		HashSet<RingChunkCoordinate> destination,
		RingCanonicalPosition canonicalStart,
		Vector3 predictionOffset,
		int radiusAround,
		int radiusAcross,
		int radiusVertical)
	{
		float distance = predictionOffset.Length();
		int steps = Mathf.Clamp(
			Mathf.CeilToInt(distance / Mathf.Max(16.0f, PredictionCorridorStepMeters)),
			1,
			Mathf.Max(1, MaxPredictionCorridorSteps));
		for (int step = 1; step <= steps; step++)
		{
			float t = step / (float)steps;
			RingChunkCoordinate corridorCenter = CanonicalPositionToChunkCoordinate(canonicalStart.AddLocal(predictionOffset * t));
			destination.UnionWith(BuildChunkSetAroundCenter(corridorCenter, radiusAround, radiusAcross, radiusVertical));
		}
	}

	public RingChunkCoordinate CanonicalPositionToChunkCoordinate(in RingCanonicalPosition canonicalPosition)
	{
		long aroundVoxel = (long)Math.Floor(canonicalPosition.AroundMeters / VoxelConstants.VoxelSizeMeters);
		int acrossVoxel = checked((int)Math.Floor(canonicalPosition.AcrossMeters / VoxelConstants.VoxelSizeMeters));
		int verticalVoxel = checked((int)Math.Floor(canonicalPosition.VerticalMeters / VoxelConstants.VoxelSizeMeters));
		return CanonicalizeChunkCoordinate(RingCoordMath.ToChunkCoordinate(
			new RingVoxelCoordinate(aroundVoxel, acrossVoxel, verticalVoxel),
			GetAroundRingVoxelLength()));
	}

	private void ResolveStreamingVelocityProvider()
	{
		if (_streamingVelocityProvider != null && GodotObject.IsInstanceValid(_streamingVelocityProvider))
		{
			return;
		}

		ResolveStreamingFocusNode();
		Node? current = _streamingFocusNode;
		while (current != null)
		{
			if (current is RingVoxelExplorerController provider)
			{
				_streamingVelocityProvider = provider;
				return;
			}

			current = current.GetParent();
		}
	}

	private void ResolveStreamingFocusNode()
	{
		if (_streamingFocusNode != null)
		{
			return;
		}

		if (StreamingFocusPath.IsEmpty)
		{
			return;
		}

		_streamingFocusNode = GetNodeOrNull<Node3D>(StreamingFocusPath);
	}

	private VoxelChunkNode EnsureChunkLoaded(in RingChunkCoordinate coordinate, bool buildCollision = true)
	{
		if (DormantEditableBackend)
			throw new InvalidOperationException("Legacy voxel chunk loading is disabled while SmoothTerrainGrid owns terrain presentation.");

		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
		CancelPendingAsyncVoxelBuild(canonical);
		_knownEmptyStreamingChunks.Remove(canonical);
		_knownSolidStreamingChunks.Remove(canonical);

		if (_chunks.TryGetValue(canonical, out VoxelChunkNode? existing))
		{
			return existing;
		}

		VoxelChunkNode chunk = CreateChunkNode(canonical, buildCollision);
		_chunks[canonical] = chunk;
		_voxelVisualCoverageRevision++;
		OnChunkLoadedPhase5(canonical);
		OnFluidChunkLoaded(canonical);
		if (!buildCollision)
			TryReleaseUneditedVisualChunkData(canonical, chunk);

		return chunk;
	}

	private bool LoadStreamingChunkIfNeeded(in RingChunkCoordinate coordinate, bool buildCollision)
	{
		if (DormantEditableBackend)
			return false;

		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
		if (EnableAsyncVoxelMeshing && EnableChunkStreaming)
		{
			if (SkipKnownEmptyStreamingChunks
				&& _knownEmptyStreamingChunks.Contains(canonical))
			{
				return false;
			}
			if (!buildCollision
				&& SkipKnownSolidStreamingChunks
				&& _knownSolidStreamingChunks.Contains(canonical))
			{
				return false;
			}
			if (!HasAsyncVoxelWorkerCapacity())
			{
				DeferAsyncVoxelBuild(canonical);
				return false;
			}
			return ScheduleAsyncVoxelBuild(canonical);
		}

		if (SkipKnownEmptyStreamingChunks && IsBaselineChunkEntirelyAir(canonical))
		{
			_knownEmptyStreamingChunks.Add(canonical);
			return false;
		}
		if (!buildCollision
			&& SkipKnownSolidStreamingChunks
			&& IsBaselineChunkFullyOccludedSolid(canonical))
		{
			_knownSolidStreamingChunks.Add(canonical);
			return false;
		}

		EnsureChunkLoaded(canonical, buildCollision);
		return true;
	}

	private bool IsBaselineChunkEntirelyAir(in RingChunkCoordinate coordinate)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
		if (_chunkOverrides.TryGetValue(canonical, out Dictionary<int, ushort>? overrides) && overrides.Count > 0)
		{
			return false;
		}

		if (!UseDeterministicTerrainGeneration || TerrainSettings == null)
		{
			return false;
		}

		int globalBaseY = canonical.VerticalChunk * VoxelConstants.ChunkSizeY;
		int globalEndYExclusive = globalBaseY + VoxelConstants.ChunkSizeY;
		int globalAcrossStart = canonical.AcrossRingChunk * VoxelConstants.ChunkSizeZ;
		int globalAcrossEnd = globalAcrossStart + VoxelConstants.ChunkSizeZ - 1;
		if (ChunkOverlapsStructuralUnderside(globalBaseY, globalEndYExclusive)
			&& ChunkOverlapsStructuralShellAcross(globalAcrossStart, globalAcrossEnd))
		{
			return false;
		}
		if (IsAcrossVoxelInStructuralHull(globalAcrossStart)
			|| IsAcrossVoxelInStructuralHull(globalAcrossEnd))
		{
			return globalEndYExclusive <= GetStructuralBottomGlobalY()
				|| globalBaseY > GetStructuralWallTopGlobalY();
		}

		if (!IsAcrossVoxelWithinRingBounds(globalAcrossStart))
		{
			return true;
		}

		if (globalEndYExclusive <= MinExcavationGlobalY)
		{
			return true;
		}

		EnsureTerrainSampler();
		int maximumPossibleSurface = _terrainSampler?.MaximumPossibleSurfaceHeight
			?? MaxBuildGlobalY;
		if (globalBaseY > maximumPossibleSurface)
		{
			return true;
		}

		if (_terrainSampler == null)
		{
			return false;
		}

		int[] surfaceTile = GetOrBuildBaselineSurfaceTile(canonical);
		for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
		for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
		{
			if (surfaceTile[z * VoxelConstants.ChunkSizeX + x] >= globalBaseY)
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Returns true only when a baseline chunk is solid on every voxel and is
	/// also surrounded by baseline solid voxels. Such a chunk cannot contribute
	/// a visible face, so it can remain procedural metadata until it enters the
	/// collision/edit bubble.
	/// </summary>
	private bool IsBaselineChunkFullyOccludedSolid(in RingChunkCoordinate coordinate)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
		if (!UseDeterministicTerrainGeneration
			|| TerrainSettings == null
			|| !IsChunkWithinRingAcrossBounds(canonical)
			|| !IsChunkWithinVerticalBounds(canonical)
			|| HasOverridesInChunkOrImmediateNeighbors(canonical))
		{
			return false;
		}

		int globalBaseY = canonical.VerticalChunk * VoxelConstants.ChunkSizeY;
		int globalEndYExclusive = globalBaseY + VoxelConstants.ChunkSizeY;
		int globalTopY = globalEndYExclusive - 1;
		if (globalBaseY <= MinExcavationGlobalY || globalEndYExclusive > MaxBuildGlobalY)
		{
			return false;
		}

		int globalAcrossStart = canonical.AcrossRingChunk * VoxelConstants.ChunkSizeZ;
		int globalAcrossEnd = globalAcrossStart + VoxelConstants.ChunkSizeZ - 1;
		if (IsAcrossVoxelInStructuralHull(globalAcrossStart)
			|| IsAcrossVoxelInStructuralHull(globalAcrossEnd)
			|| !IsAcrossVoxelWithinRingBounds(globalAcrossStart)
			|| !IsAcrossVoxelWithinRingBounds(globalAcrossEnd))
		{
			return false;
		}

		// Requiring one complete solid layer above the chunk preserves exposed
		// top faces when a surface lies exactly on a chunk boundary.
		int[] surfaceTile = GetOrBuildBaselineSurfaceTile(canonical);
		for (int i = 0; i < surfaceTile.Length; i++)
		{
			if (surfaceTile[i] < globalEndYExclusive)
				return false;
		}

		// A vertically full chunk can still own a visible cliff face. Check the
		// four neighboring height strips before virtualizing it.
		for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
		{
			if (GetCachedBaselineSurfaceHeight(canonical, -1, z) < globalTopY
				|| GetCachedBaselineSurfaceHeight(canonical, VoxelConstants.ChunkSizeX, z) < globalTopY)
			{
				return false;
			}
		}
		for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
		{
			if (GetCachedBaselineSurfaceHeight(canonical, x, -1) < globalTopY
				|| GetCachedBaselineSurfaceHeight(canonical, x, VoxelConstants.ChunkSizeZ) < globalTopY)
			{
				return false;
			}
		}

		return true;
	}

	private bool HasOverridesInChunkOrImmediateNeighbors(in RingChunkCoordinate coordinate)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
		if (_chunkOverrides.ContainsKey(canonical)) return true;
		return _chunkOverrides.ContainsKey(OffsetChunkCoordinate(canonical, -1, 0, 0))
			|| _chunkOverrides.ContainsKey(OffsetChunkCoordinate(canonical, +1, 0, 0))
			|| _chunkOverrides.ContainsKey(OffsetChunkCoordinate(canonical, 0, -1, 0))
			|| _chunkOverrides.ContainsKey(OffsetChunkCoordinate(canonical, 0, +1, 0))
			|| _chunkOverrides.ContainsKey(OffsetChunkCoordinate(canonical, 0, 0, -1))
			|| _chunkOverrides.ContainsKey(OffsetChunkCoordinate(canonical, 0, 0, +1));
	}

	private int GetCachedBaselineSurfaceHeight(
		in RingChunkCoordinate baseCoordinate,
		int localX,
		int localZ)
	{
		RingChunkCoordinate target = CanonicalizeChunkCoordinate(baseCoordinate);
		while (localX < 0)
		{
			target = OffsetChunkCoordinate(target, -1, 0, 0);
			localX += VoxelConstants.ChunkSizeX;
		}
		while (localX >= VoxelConstants.ChunkSizeX)
		{
			target = OffsetChunkCoordinate(target, +1, 0, 0);
			localX -= VoxelConstants.ChunkSizeX;
		}
		while (localZ < 0)
		{
			target = OffsetChunkCoordinate(target, 0, -1, 0);
			localZ += VoxelConstants.ChunkSizeZ;
		}
		while (localZ >= VoxelConstants.ChunkSizeZ)
		{
			target = OffsetChunkCoordinate(target, 0, +1, 0);
			localZ -= VoxelConstants.ChunkSizeZ;
		}

		int globalZ = target.AcrossRingChunk * VoxelConstants.ChunkSizeZ + localZ;
		if (!IsAcrossVoxelWithinRingBounds(globalZ)
			|| IsAcrossVoxelInStructuralHull(globalZ))
		{
			return int.MinValue;
		}

		int[] tile = GetOrBuildBaselineSurfaceTile(target);
		return tile[localZ * VoxelConstants.ChunkSizeX + localX];
	}

	private void UnloadChunk(in RingChunkCoordinate coordinate)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);

		if (!_chunks.TryGetValue(canonical, out VoxelChunkNode? chunk))
		{
			return;
		}

		_chunks.Remove(canonical);
		_voxelVisualCoverageRevision++;
		OnFluidChunkUnloaded(canonical);

		chunk.PrepareForPool();
		if (_chunkPool.Count < Mathf.Max(0, MaxPooledChunkNodes))
			_chunkPool.Enqueue(chunk);
		else
		{
			RemoveChild(chunk);
			chunk.Free();
		}

		OnChunkUnloadedPhase5(canonical);
	}

	private HashSet<RingChunkCoordinate> BuildChunkSetAroundCenter(
		in RingChunkCoordinate center,
		int radiusAround,
		int radiusAcross,
		int radiusVertical)
	{
		var set = new HashSet<RingChunkCoordinate>();

		RingChunkCoordinate canonicalCenter = CanonicalizeChunkCoordinate(center);

		for (int vertical = -radiusVertical; vertical <= radiusVertical; vertical++)
		{
			for (int across = -radiusAcross; across <= radiusAcross; across++)
			{
				for (int around = -radiusAround; around <= radiusAround; around++)
				{
					RingChunkCoordinate candidate = OffsetChunkCoordinate(canonicalCenter, around, across, vertical);
					if (IsChunkWithinRingAcrossBounds(candidate) && IsChunkWithinVerticalBounds(candidate))
					{
						set.Add(candidate);
					}
				}
			}
		}

		return set;
	}

	public VoxelChunkDeltaSnapshot BuildSparseChunkDeltaSnapshot()
	{
		var snapshot = new VoxelChunkDeltaSnapshot();

		foreach ((RingChunkCoordinate coord, Dictionary<int, ushort> overrides) in _chunkOverrides)
		{
			if (overrides.Count == 0)
			{
				continue;
			}

			snapshot.ChunkOverrides[ChunkCoordToKey(coord)] = new Dictionary<int, ushort>(overrides);
		}

		foreach ((RingTerrainSampleCoordinate sample, float densityDelta) in _naturalTerrainSdfDeltas)
		{
			snapshot.NaturalTerrainSdfDeltas.Add(new NaturalTerrainSdfDeltaRecord
			{
			AroundVoxel = sample.AroundVoxel,
			VerticalVoxel = sample.VerticalVoxel,
			AcrossVoxel = sample.AcrossVoxel,
				DensityDelta = densityDelta
			});
		}

		return snapshot;
	}

	/// <summary>
	/// Captures only chunks changed since the previous capture. An empty override dictionary
	/// is intentional: it clears a chunk whose edits were reverted to generated terrain.
	/// </summary>
	public VoxelChunkDeltaSnapshot CaptureDirtyChunkDeltaSnapshot()
	{
		var snapshot = new VoxelChunkDeltaSnapshot();
		foreach (RingChunkCoordinate coord in _dirtyOverrideChunks)
		{
			string key = ChunkCoordToKey(coord);
			snapshot.ChunkOverrides[key] = _chunkOverrides.TryGetValue(coord, out Dictionary<int, ushort>? overrides)
				? new Dictionary<int, ushort>(overrides)
				: new Dictionary<int, ushort>();
		}
		_dirtyOverrideChunks.Clear();
		foreach (RingTerrainSampleCoordinate sample in _dirtyNaturalTerrainSdfSamples)
		{
			snapshot.NaturalTerrainSdfDeltas.Add(new NaturalTerrainSdfDeltaRecord
			{
			AroundVoxel = sample.AroundVoxel,
			VerticalVoxel = sample.VerticalVoxel,
			AcrossVoxel = sample.AcrossVoxel,
				DensityDelta = _naturalTerrainSdfDeltas.TryGetValue(sample, out float densityDelta)
					? densityDelta
					: 0.0f
			});
		}
		_dirtyNaturalTerrainSdfSamples.Clear();
		return snapshot;
	}

	public void RestoreDirtyChunkDeltaSnapshot(VoxelChunkDeltaSnapshot snapshot)
	{
		foreach (string chunkKey in snapshot.ChunkOverrides.Keys)
		{
			if (TryParseChunkCoordKey(chunkKey, out RingChunkCoordinate coord))
				_dirtyOverrideChunks.Add(CanonicalizeChunkCoordinate(coord));
		}
		foreach (NaturalTerrainSdfDeltaRecord record in snapshot.NaturalTerrainSdfDeltas)
		{
			_dirtyNaturalTerrainSdfSamples.Add(CanonicalizeNaturalTerrainSdfSample(
				record.AroundVoxel,
				record.VerticalVoxel,
				record.AcrossVoxel));
		}
	}

	public string BuildSparseChunkDeltaJson(bool writeIndented = false)
	{
		VoxelChunkDeltaSnapshot snapshot = BuildSparseChunkDeltaSnapshot();
		var options = new JsonSerializerOptions
		{
			WriteIndented = writeIndented
		};

		return JsonSerializer.Serialize(snapshot, options);
	}

	public Error SaveSparseChunkDeltaJson(string savePath = DefaultDeltaSavePath)
	{
		string json = BuildSparseChunkDeltaJson(writeIndented: false);

		FileAccess? file = FileAccess.Open(savePath, FileAccess.ModeFlags.Write);
		if (file == null)
		{
			return FileAccess.GetOpenError();
		}

		file.StoreString(json);
		return Error.Ok;
	}

	public Error LoadSparseChunkDeltaJson(string savePath = DefaultDeltaSavePath, bool flushRebuildQueueAfterApply = true)
	{
		if (!FileAccess.FileExists(savePath))
		{
			return Error.FileNotFound;
		}

		FileAccess? file = FileAccess.Open(savePath, FileAccess.ModeFlags.Read);
		if (file == null)
		{
			return FileAccess.GetOpenError();
		}

		string json = file.GetAsText();
		bool applied = TryApplySparseChunkDeltaJson(json, flushRebuildQueueAfterApply);
		return applied ? Error.Ok : Error.ParseError;
	}

	public bool TryApplySparseChunkDeltaJson(string json, bool flushRebuildQueueAfterApply = true)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return false;
		}

		VoxelChunkDeltaSnapshot? snapshot;
		try
		{
			snapshot = JsonSerializer.Deserialize<VoxelChunkDeltaSnapshot>(json);
		}
		catch (JsonException)
		{
			return false;
		}

		if (snapshot == null)
		{
			return false;
		}

		ApplySparseChunkDeltaSnapshot(snapshot, flushRebuildQueueAfterApply);
		return true;
	}

	public void ApplySparseChunkDeltaSnapshot(VoxelChunkDeltaSnapshot snapshot, bool flushRebuildQueueAfterApply = true)
	{
		InvalidateAllAsyncVoxelBuilds();
		_rainSurfaceEditVersion++;
		_chunkOverrides.Clear();
		_rainColumnTops.Clear();
		_dirtyOverrideChunks.Clear();
		_naturalTerrainSdfDeltas.Clear();
		_dirtyNaturalTerrainSdfSamples.Clear();
		_knownEmptyStreamingChunks.Clear();
		_knownSolidStreamingChunks.Clear();
		foreach ((string chunkKey, Dictionary<int, ushort> overrides) in snapshot.ChunkOverrides)
		{
			if (!TryParseChunkCoordKey(chunkKey, out RingChunkCoordinate coord))
			{
				continue;
			}

			if (overrides.Count == 0)
			{
				continue;
			}

			RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coord);
			_chunkOverrides[canonical] = new Dictionary<int, ushort>(overrides);
		}
		RebuildRainColumnTops();

		foreach (NaturalTerrainSdfDeltaRecord record in snapshot.NaturalTerrainSdfDeltas)
		{
			if (!float.IsFinite(record.DensityDelta) || Mathf.IsZeroApprox(record.DensityDelta))
				continue;
			_naturalTerrainSdfDeltas[CanonicalizeNaturalTerrainSdfSample(
				record.AroundVoxel,
				record.VerticalVoxel,
				record.AcrossVoxel)] = record.DensityDelta;
		}

		foreach ((RingChunkCoordinate coord, VoxelChunkNode chunk) in _chunks)
		{
			PopulateChunkBaseline(chunk, coord);
			ApplyOverridesToChunkData(coord, chunk);
			QueueRebuild(coord);
		}

		if (flushRebuildQueueAfterApply)
		{
			FlushRebuildQueue();
		}

		InvalidateLodSurfaceEditSnapshot();
		_hasLastStreamingCenter = false;
		OnSparseSnapshotAppliedPhase5();
	}

	private VoxelChunkNode CreateChunkNode(RingChunkCoordinate coordinate, bool buildCollision = true)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
		VoxelChunkNode chunk = AcquireConfiguredChunkNode(canonical, buildCollision);
		PopulateChunkBaseline(chunk, canonical);
		ApplyOverridesToChunkData(canonical, chunk);
		chunk.Rebuild();
		return chunk;
	}

	private VoxelChunkNode CreateChunkNodeFromBuild(
		in RingChunkCoordinate coordinate,
		bool buildCollision,
		VoxelChunkData data,
		in MeshBuildData meshBuild)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
		VoxelChunkNode chunk = AcquireConfiguredChunkNode(canonical, buildCollision);
		chunk.InstallStreamingBuild(data, meshBuild);
		return chunk;
	}

	private VoxelChunkNode AcquireConfiguredChunkNode(
		in RingChunkCoordinate canonical,
		bool buildCollision)
	{
		VoxelChunkNode chunk;
		if (_chunkPool.Count > 0)
		{
			chunk = _chunkPool.Dequeue();
			chunk.Name = $"Chunk_{canonical.AroundRingChunk}_{canonical.AcrossRingChunk}_{canonical.VerticalChunk}";
			chunk.GenerateOnReady = false;
			chunk.InitialSolidHeight = InitialSolidHeight;
			chunk.BlockColorMap = BlockColorMap;
		}
		else
		{
			chunk = new VoxelChunkNode
			{
				Name = $"Chunk_{canonical.AroundRingChunk}_{canonical.AcrossRingChunk}_{canonical.VerticalChunk}",
				GenerateOnReady = false,
				InitialSolidHeight = InitialSolidHeight,
				BlockColorMap = BlockColorMap
			};
		}

		ulong treeRegistrationStartUsec = Time.GetTicksUsec();
		if (chunk.GetParent() == null)
			AddChild(chunk);
		LastVoxelNodeTreeRegistrationMilliseconds =
			(Time.GetTicksUsec() - treeRegistrationStartUsec) / 1000.0;
		chunk.PrepareForReuse(buildCollision);
		chunk.Visible = !_fastTravelChunkMeshesHidden;
		chunk.TopLevel = UseRingCurvedPlacement && UseCameraRelativeTopLevelChunks;
		if (UseRingCurvedPlacement && UseCameraRelativeTopLevelChunks)
		{
			chunk.GlobalTransform = ChunkCoordinateToWorldTransform(canonical);
		}
		else
		{
			chunk.Transform = ChunkCoordinateToLocalTransform(canonical);
		}
		ulong materialStartUsec = Time.GetTicksUsec();
		chunk.SetSharedMaterialOverride(GetVoxelChunkMaterial());
		LastVoxelMaterialPreparationMilliseconds =
			(Time.GetTicksUsec() - materialStartUsec) / 1000.0;
		UpdateSmoothTerrainShadowCasting(chunk, buildCollision);
		ConfigureChunkCurvatureWarp(chunk, canonical);
		RingChunkCoordinate samplerCoordinate = canonical;
		chunk.SetVoxelDataRestorer(() => CreateRegeneratedVoxelData(samplerCoordinate));
		chunk.SetExternalBlockSampler((x, y, z) => SampleBlockAcrossGrid(samplerCoordinate, x, y, z));
		return chunk;
	}

	private VoxelChunkData CreateRegeneratedVoxelData(in RingChunkCoordinate coordinate)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
		var data = new VoxelChunkData();
		PopulateChunkBaseline(data, canonical);
		ApplyOverridesToChunkData(canonical, data);
		return data;
	}

	private void UpdateSmoothTerrainShadowCasting(
		VoxelChunkNode chunk,
		bool inCollisionRange)
	{
		bool castShadows = !UseSmoothNaturalTerrainPilot
			|| !LimitSmoothTerrainShadowsToCollisionRange
			|| inCollisionRange;
		chunk.SetTerrainShadowCasting(castShadows);
	}

	private Material? GetVoxelChunkMaterial()
	{
		if (!EnableRingHemisphereLighting || !UseRingCurvedPlacement || !UseHabitableRingAxisLayout)
		{
			return null;
		}

		if (_voxelRingMaterial == null)
		{
			Shader? shader = GD.Load<Shader>("res://materials/voxel_ring_terrain.gdshader");
			if (shader == null)
			{
				GD.PushWarning("Voxel ring hemisphere shader could not be loaded; using standard lighting.");
				return null;
			}

			_voxelRingMaterial = new ShaderMaterial { Shader = shader };
			_voxelRingMaterial.SetShaderParameter(
				"pilot_grass_albedo",
				GD.Load<Texture2D>("res://materials/third_party/voxelgame/grass_albedo.png"));
			_voxelRingMaterial.SetShaderParameter(
				"pilot_dirt_albedo",
				GD.Load<Texture2D>("res://materials/third_party/voxelgame/dirt_albedo.png"));
			_voxelRingMaterial.SetShaderParameter("biome_desert_albedo", GD.Load<Texture2D>("res://materials/biomes/desert_sand_albedo.png"));
			_voxelRingMaterial.SetShaderParameter("biome_forest_albedo", GD.Load<Texture2D>("res://materials/biomes/forest_floor_albedo.png"));
			_voxelRingMaterial.SetShaderParameter("biome_tundra_albedo", GD.Load<Texture2D>("res://materials/biomes/tundra_ground_albedo.png"));
			_voxelRingMaterial.SetShaderParameter("biome_alpine_albedo", GD.Load<Texture2D>("res://materials/biomes/alpine_rock_albedo.png"));
			// Legacy block meshes use their colour channel for BlockType palette
			// colours, so they intentionally retain the material-safe fallback.
			_voxelRingMaterial.SetShaderParameter("biome_texture_weights_enabled", false);
			RingAtmosphereModel.ConfigureMaterial(_voxelRingMaterial, GetEffectiveRingRadiusMeters64());
			global::RingworldTechDemo.RingCloudShadowProjector.Register(_voxelRingMaterial);
		}

		UpdateVoxelRingLightingMaterial();
		return _voxelRingMaterial;
	}

	private void UpdateVoxelRingLightingMaterial()
	{
		if (_voxelRingMaterial == null)
		{
			return;
		}

		if (_ringSunLight == null || !GodotObject.IsInstanceValid(_ringSunLight))
		{
			_ringSunLight = GetNodeOrNull<DirectionalLight3D>(RingSunLightPath);
		}

		Vector3 ringCenter = GetCanonicalRingCenterWorld();
		if (!_lastVoxelMaterialRingCenter.IsEqualApprox(ringCenter))
		{
			_voxelRingMaterial.SetShaderParameter("ring_center", ringCenter);
			_lastVoxelMaterialRingCenter = ringCenter;
		}

		Vector3 cameraPosition = _streamingFocusNode?.GlobalPosition ?? Vector3.Zero;
		if (!_lastVoxelMaterialCameraPosition.IsEqualApprox(cameraPosition))
		{
			_voxelRingMaterial.SetShaderParameter("camera_world_position", cameraPosition);
			_lastVoxelMaterialCameraPosition = cameraPosition;
		}

		Vector3 sunDirection = _ringSunLight?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
		if (!_lastVoxelMaterialSunDirection.IsEqualApprox(sunDirection))
		{
			_voxelRingMaterial.SetShaderParameter("sun_direction", sunDirection);
			_lastVoxelMaterialSunDirection = sunDirection;
		}

		var configuration = new VoxelMaterialLightingConfiguration(
			RingDayStartDot,
			Mathf.Max(RingDayStartDot + 0.001f, RingDayFullDot),
			new Vector3(RingNightTint.R, RingNightTint.G, RingNightTint.B),
			RingNightDirectLightFactor,
			RingDayDirectLightFactor,
			RingShadowFloor,
			UseSmoothNaturalTerrainPilot && UseSmoothTerrainPilotTextures,
			Mathf.Clamp(SmoothTerrainPilotTextureScale, 0.01f, 2.0f));
		if (_hasVoxelMaterialLightingConfiguration && configuration == _lastVoxelMaterialLightingConfiguration) return;
		_voxelRingMaterial.SetShaderParameter("day_start_dot", configuration.DayStartDot);
		_voxelRingMaterial.SetShaderParameter("day_full_dot", configuration.DayFullDot);
		_voxelRingMaterial.SetShaderParameter("night_tint", configuration.NightTint);
		_voxelRingMaterial.SetShaderParameter("night_direct_light_factor", configuration.NightDirectLightFactor);
		_voxelRingMaterial.SetShaderParameter("day_direct_light_factor", configuration.DayDirectLightFactor);
		_voxelRingMaterial.SetShaderParameter("shadow_floor", configuration.ShadowFloor);
		_voxelRingMaterial.SetShaderParameter("pilot_triplanar_enabled", configuration.PilotTriplanarEnabled);
		_voxelRingMaterial.SetShaderParameter("pilot_texture_scale", configuration.PilotTextureScale);
		_lastVoxelMaterialLightingConfiguration = configuration;
		_hasVoxelMaterialLightingConfiguration = true;
	}

	private void PopulateChunkBaseline(VoxelChunkNode chunk, RingChunkCoordinate coordinate)
		=> PopulateChunkBaseline(chunk.Data, coordinate);

	private void PopulateChunkBaseline(VoxelChunkData data, RingChunkCoordinate coordinate)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
		if (!UseDeterministicTerrainGeneration || TerrainSettings == null)
		{
			data.FillFromSampler((x, y, z) => GetGeneratedBaselineBlock(canonical, x, y, z));
			return;
		}

		EnsureTerrainSampler();
		if (_terrainSampler == null)
		{
			data.Fill(BlockType.Air);
			return;
		}

		int globalBaseY = canonical.VerticalChunk * VoxelConstants.ChunkSizeY;
		int globalBaseZ = canonical.AcrossRingChunk * VoxelConstants.ChunkSizeZ;
		int soilDepth = Mathf.Max(1, TerrainSettings.SoilDepthVoxels);
		int bedrockY = TerrainSettings.BedrockY;
		int[] surfaceTile = GetOrBuildBaselineSurfaceTile(canonical);

		// Surface height is column data: sample it once per X/Z column instead
		// of once for every voxel in the 32-high column.
		for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
		for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
		{
			int globalZ = globalBaseZ + z;
			if (IsAcrossVoxelInStructuralHull(globalZ))
			{
				for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
				{
					int globalY = globalBaseY + y;
					data.SetBlock(x, y, z, IsVerticalVoxelWithinStructuralHull(globalY) ? BlockType.Bedrock : BlockType.Air);
				}
				continue;
			}

			if (!IsAcrossVoxelWithinRingBounds(globalZ))
			{
				for (int y = 0; y < VoxelConstants.ChunkSizeY; y++) data.SetBlock(x, y, z, BlockType.Air);
				continue;
			}

			int surfaceY = surfaceTile[z * VoxelConstants.ChunkSizeX + x];
			for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
			{
				int globalY = globalBaseY + y;
				BlockType block = IsStructuralBedrockVoxel(globalY, globalZ)
					? BlockType.Bedrock
					: globalY < MinExcavationGlobalY
						? BlockType.Air
						: globalY <= bedrockY
							? BlockType.Bedrock
							: globalY > surfaceY
								? BlockType.Air
								: surfaceY - globalY < soilDepth ? BlockType.Soil : BlockType.Rock;
				data.SetBlock(x, y, z, block);
			}
		}
	}

	private BlockType GetGeneratedBaselineBlock(in RingChunkCoordinate coordinate, int localX, int localY, int localZ)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);

		if (!UseDeterministicTerrainGeneration || TerrainSettings == null)
		{
			return VoxelChunkData.GetFlatTestTerrainBlock(localY, InitialSolidHeight);
		}

		long globalX = canonical.AroundRingChunk * VoxelConstants.ChunkSizeX + localX;
		int globalY = canonical.VerticalChunk * VoxelConstants.ChunkSizeY + localY;
		int globalZ = canonical.AcrossRingChunk * VoxelConstants.ChunkSizeZ + localZ;

		if (IsStructuralBedrockVoxel(globalY, globalZ))
		{
			return BlockType.Bedrock;
		}

		if (globalY < MinExcavationGlobalY)
		{
			return BlockType.Air;
		}

		if (IsAcrossVoxelInStructuralHull(globalZ))
		{
			return IsVerticalVoxelWithinStructuralHull(globalY) ? BlockType.Bedrock : BlockType.Air;
		}

		if (globalY > MaxBuildGlobalY)
		{
			return BlockType.Air;
		}

		if (!IsAcrossVoxelWithinRingBounds(globalZ))
		{
			return BlockType.Air;
		}

		EnsureTerrainSampler();
		return _terrainSampler?.GetBaselineBlock(globalX, globalY, globalZ) ?? BlockType.Air;
	}

	private int GetSurfaceHeight(long globalAroundVoxel, int globalAcrossVoxel)
	{
		if (TerrainSettings == null)
		{
			return InitialSolidHeight - 1;
		}

		EnsureTerrainSampler();
		return _terrainSampler?.GetSurfaceHeight(globalAroundVoxel, globalAcrossVoxel)
			?? InitialSolidHeight - 1;
	}

	private void RebuildTerrainNoises()
	{
		_terrainSampler?.Dispose();
		_terrainSampler = null;
		lock (_baselineSurfaceTileLock)
		{
			_baselineSurfaceTileCache.Clear();
			_baselineSurfaceTileOrder.Clear();
		}

		if (!UseDeterministicTerrainGeneration || TerrainSettings == null)
		{
			return;
		}

		RingVerticalDomainConfiguration domain = GetVerticalDomainConfiguration();
		int acrossWidth = Mathf.Max(1, Mathf.RoundToInt(RingAcrossWidthMeters));
		var cavePlacement = new CaveFeaturePlacementPolicy(
			acrossWidth,
			domain.TerrainBottomGlobalY,
			domain.ProtectedRoofTopGlobalY,
			domain.LowerHullTopGlobalY,
			domain.StructuralBottomGlobalY,
			Math.Max(TerrainSettings.BedrockY + 1, TerrainSettings.DeepOceanFloorY));
		_terrainSampler = new RingTerrainSampler(
			TerrainSettings,
			UseRingCurvedPlacement,
			GetAroundRingVoxelLength(),
			GetEffectiveRingRadiusMeters64(),
			acrossWidth,
			MaxBuildGlobalY,
			cavePlacement);
	}

	private int[] GetOrBuildBaselineSurfaceTile(in RingChunkCoordinate coordinate)
	{
		var key = new RingTerrainColumnCoordinate(coordinate.AroundRingChunk, coordinate.AcrossRingChunk);
		lock (_baselineSurfaceTileLock)
		{
			if (_baselineSurfaceTileCache.TryGetValue(key, out int[]? cached)) return cached;
		}

		EnsureTerrainSampler();
		var generated = new int[VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeZ];
		if (_terrainSampler != null)
		{
			long globalBaseX = coordinate.AroundRingChunk * VoxelConstants.ChunkSizeX;
			int globalBaseZ = coordinate.AcrossRingChunk * VoxelConstants.ChunkSizeZ;
			for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
			for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
			{
				generated[z * VoxelConstants.ChunkSizeX + x] = _terrainSampler.GetSurfaceHeight(
					globalBaseX + x,
					globalBaseZ + z);
			}
		}

		lock (_baselineSurfaceTileLock)
		{
			if (_baselineSurfaceTileCache.TryGetValue(key, out int[]? existing)) return existing;
			int capacity = Mathf.Max(32, SurfaceTileCacheCapacity);
			while (_baselineSurfaceTileCache.Count >= capacity && _baselineSurfaceTileOrder.Count > 0)
			{
				RingTerrainColumnCoordinate oldest = _baselineSurfaceTileOrder.Dequeue();
				_baselineSurfaceTileCache.Remove(oldest);
			}
			_baselineSurfaceTileCache[key] = generated;
			_baselineSurfaceTileOrder.Enqueue(key);
			return generated;
		}
	}

	private void EnsureTerrainSampler()
	{
		if (_terrainSampler == null && UseDeterministicTerrainGeneration && TerrainSettings != null)
		{
			RebuildTerrainNoises();
		}
	}

	private BlockType SampleBlockAcrossGrid(in RingChunkCoordinate baseCoord, int x, int y, int z)
	{
		RingChunkCoordinate targetCoord = CanonicalizeChunkCoordinate(baseCoord);
		int localX = x;
		int localY = y;
		int localZ = z;

		while (localX < 0)
		{
			targetCoord = OffsetChunkCoordinate(targetCoord, -1, 0, 0);
			localX += VoxelConstants.ChunkSizeX;
		}
		while (localX >= VoxelConstants.ChunkSizeX)
		{
			targetCoord = OffsetChunkCoordinate(targetCoord, +1, 0, 0);
			localX -= VoxelConstants.ChunkSizeX;
		}

		while (localY < 0)
		{
			targetCoord = OffsetChunkCoordinate(targetCoord, 0, 0, -1);
			localY += VoxelConstants.ChunkSizeY;
		}
		while (localY >= VoxelConstants.ChunkSizeY)
		{
			targetCoord = OffsetChunkCoordinate(targetCoord, 0, 0, +1);
			localY -= VoxelConstants.ChunkSizeY;
		}

		while (localZ < 0)
		{
			targetCoord = OffsetChunkCoordinate(targetCoord, 0, -1, 0);
			localZ += VoxelConstants.ChunkSizeZ;
		}
		while (localZ >= VoxelConstants.ChunkSizeZ)
		{
			targetCoord = OffsetChunkCoordinate(targetCoord, 0, +1, 0);
			localZ -= VoxelConstants.ChunkSizeZ;
		}

		if (_chunks.TryGetValue(targetCoord, out VoxelChunkNode? targetChunk)
			&& targetChunk.TryGetRetainedBlock(
				new Vector3I(localX, localY, localZ),
				out BlockType retainedBlock))
		{
			return retainedBlock;
		}

		int localIndex = VoxelChunkData.ToLinearIndex(localX, localY, localZ);
		if (_chunkOverrides.TryGetValue(targetCoord, out Dictionary<int, ushort>? overrides)
			&& overrides.TryGetValue(localIndex, out ushort blockId))
		{
			return (BlockType)blockId;
		}

		// An unloaded neighbor is still authoritative procedural terrain. Treating
		// it as air creates temporary chunk-border faces and forces rebuilds when
		// that neighbor later becomes resident.
		return GetCachedGeneratedBaselineBlock(targetCoord, localX, localY, localZ);
	}

	private BlockType GetCachedGeneratedBaselineBlock(
		in RingChunkCoordinate coordinate,
		int localX,
		int localY,
		int localZ)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
		if (!UseDeterministicTerrainGeneration || TerrainSettings == null)
			return VoxelChunkData.GetFlatTestTerrainBlock(localY, InitialSolidHeight);
		if (!IsChunkWithinVerticalBounds(canonical))
			return BlockType.Air;

		int globalY = canonical.VerticalChunk * VoxelConstants.ChunkSizeY + localY;
		int globalZ = canonical.AcrossRingChunk * VoxelConstants.ChunkSizeZ + localZ;
		if (IsStructuralBedrockVoxel(globalY, globalZ))
			return BlockType.Bedrock;
		if (globalY < MinExcavationGlobalY
			|| globalY > MaxBuildGlobalY
			|| !IsAcrossVoxelWithinRingBounds(globalZ))
		{
			return BlockType.Air;
		}
		if (IsAcrossVoxelInStructuralHull(globalZ))
			return IsVerticalVoxelWithinStructuralHull(globalY) ? BlockType.Bedrock : BlockType.Air;

		int[] surfaceTile = GetOrBuildBaselineSurfaceTile(canonical);
		int surfaceY = surfaceTile[localZ * VoxelConstants.ChunkSizeX + localX];
		int soilDepth = Mathf.Max(1, TerrainSettings.SoilDepthVoxels);
		return globalY <= TerrainSettings.BedrockY
			? BlockType.Bedrock
			: globalY > surfaceY
				? BlockType.Air
				: surfaceY - globalY < soilDepth ? BlockType.Soil : BlockType.Rock;
	}

	private void QueueRebuild(in RingChunkCoordinate coordinate)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);

		if (!UseDebouncedRebuildQueue)
		{
			RebuildIfExistsImmediate(canonical);
			return;
		}

		if (!_chunks.ContainsKey(canonical))
		{
			return;
		}

		MarkResidentVoxelRebuildRequested(canonical);
		double dueTime = GetNowSeconds() + Mathf.Max(0.0f, RebuildDebounceSeconds);
		_queuedRebuildDeadlines[canonical] = dueTime;
	}

	private void ProcessRebuildQueue()
	{
		if (!UseDebouncedRebuildQueue || _queuedRebuildDeadlines.Count == 0)
		{
			return;
		}

		double now = GetNowSeconds();
		int frameBudget = Mathf.Max(1, MaxQueuedRebuildsPerFrame);
		var due = new List<KeyValuePair<RingChunkCoordinate, double>>();

		foreach (var pair in _queuedRebuildDeadlines)
		{
			if (pair.Value <= now)
			{
				due.Add(pair);
			}
		}

		if (due.Count == 0)
		{
			return;
		}

		due.Sort((a, b) => a.Value.CompareTo(b.Value));

		int processed = 0;
		foreach (var pair in due)
		{
			if (processed >= frameBudget)
			{
				break;
			}

			if (EnableAsyncVoxelMeshing)
			{
				if (!TryScheduleResidentVoxelRebuild(pair.Key))
					continue;
				_queuedRebuildDeadlines.Remove(pair.Key);
				processed++;
			}
			else
			{
				_queuedRebuildDeadlines.Remove(pair.Key);
				RebuildIfExistsImmediate(pair.Key);
				processed++;
			}
		}
	}

	private void RebuildIfExistsImmediate(in RingChunkCoordinate coordinate)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
		InvalidateResidentVoxelRebuild(canonical);
		if (_chunks.TryGetValue(canonical, out VoxelChunkNode? chunk))
		{
			chunk.Rebuild();
		}
	}

	private void UpdateOverrideForVoxel(in RingChunkCoordinate coord, in Vector3I localVoxel, VoxelChunkNode chunk)
	{
		// Rain/shelter caches care about non-natural construction too, unlike LOD surface heights.
		_rainSurfaceEditVersion++;
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coord);
		RingLodSurfaceEditSnapshot previousSurfaceSnapshot = CaptureLodSurfaceEditSnapshot();
		long around = canonical.AroundRingChunk * VoxelConstants.ChunkSizeX + localVoxel.X;
		int across = canonical.AcrossRingChunk * VoxelConstants.ChunkSizeZ + localVoxel.Z;
		int previousSurfaceHeight = previousSurfaceSnapshot.GetSurfaceHeight(
			_terrainSampler!,
			around,
			across);
		_dirtyOverrideChunks.Add(canonical);
		int localIndex = VoxelChunkData.ToLinearIndex(localVoxel.X, localVoxel.Y, localVoxel.Z);
		BlockType current = chunk.Data.GetBlock(localVoxel.X, localVoxel.Y, localVoxel.Z);
		BlockType baseline = GetGeneratedBaselineBlock(canonical, localVoxel.X, localVoxel.Y, localVoxel.Z);

		if (current == baseline)
		{
			if (_chunkOverrides.TryGetValue(canonical, out Dictionary<int, ushort>? existing))
			{
				existing.Remove(localIndex);
				if (existing.Count == 0)
				{
					_chunkOverrides.Remove(canonical);
				}
			}
			SetRainColumnOverride(LocalToGlobalVoxel(canonical, localVoxel), BlockType.Air);

			UpdateLodSurfaceEditSnapshotForVoxel(
				canonical,
				localVoxel,
				previousSurfaceSnapshot,
				previousSurfaceHeight);
			return;
		}

		if (!_chunkOverrides.TryGetValue(canonical, out Dictionary<int, ushort>? overrides))
		{
			overrides = new Dictionary<int, ushort>();
			_chunkOverrides[canonical] = overrides;
		}

		overrides[localIndex] = (ushort)current;
		SetRainColumnOverride(LocalToGlobalVoxel(canonical, localVoxel), current);
		UpdateLodSurfaceEditSnapshotForVoxel(
			canonical,
			localVoxel,
			previousSurfaceSnapshot,
			previousSurfaceHeight);
	}

	private void ApplyOverridesToChunkData(in RingChunkCoordinate coord, VoxelChunkNode chunk)
		=> ApplyOverridesToChunkData(coord, chunk.Data);

	private void ApplyOverridesToChunkData(in RingChunkCoordinate coord, VoxelChunkData data)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coord);
		if (!_chunkOverrides.TryGetValue(canonical, out Dictionary<int, ushort>? overrides) || overrides.Count == 0)
		{
			return;
		}

		var invalidOrBaseline = new List<int>();
		foreach ((int localIndex, ushort blockId) in overrides)
		{
			if (!VoxelChunkData.TryFromLinearIndex(localIndex, out Vector3I localVoxel))
			{
				invalidOrBaseline.Add(localIndex);
				continue;
			}

			BlockType blockType = (BlockType)blockId;
			BlockType current = data.GetBlock(localVoxel.X, localVoxel.Y, localVoxel.Z);
			RingVoxelCoordinate globalVoxel = LocalToGlobalVoxel(canonical, localVoxel);
			if (!IsVoxelEditAllowed(current, blockType, globalVoxel))
			{
				invalidOrBaseline.Add(localIndex);
				continue;
			}

			data.SetBlock(localVoxel.X, localVoxel.Y, localVoxel.Z, blockType);
		}

		foreach (int localIndex in invalidOrBaseline)
		{
			overrides.Remove(localIndex);
		}

		if (overrides.Count == 0)
		{
			_chunkOverrides.Remove(canonical);
		}
		if (invalidOrBaseline.Count > 0)
		{
			foreach (int localIndex in invalidOrBaseline)
				if (VoxelChunkData.TryFromLinearIndex(localIndex, out Vector3I local)) SetRainColumnOverride(LocalToGlobalVoxel(canonical, local), BlockType.Air);
			_rainSurfaceEditVersion++;
		}
	}

	private string ChunkCoordToKey(in RingChunkCoordinate coordinate)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
		return $"{canonical.AroundRingChunk},{canonical.AcrossRingChunk},{canonical.VerticalChunk}";
	}

	private bool IsVoxelEditAllowed(BlockType currentBlock, BlockType requestedBlock, in RingVoxelCoordinate globalVoxel)
	{
		if (currentBlock == requestedBlock)
		{
			return false;
		}

		if (EnforceExcavationLimits && globalVoxel.Vertical < MinExcavationGlobalY)
		{
			return false;
		}

		if (globalVoxel.Vertical < MinExcavationGlobalY || globalVoxel.Vertical > MaxBuildGlobalY)
		{
			return false;
		}

		// The ring uses absolute canonical build bounds so all columns share one ceiling.
		if (EnforceBuildHeightLimit && requestedBlock != BlockType.Air)
		{
			if (globalVoxel.Vertical > MaxBuildGlobalY)
			{
				return false;
			}
		}

		if (!EnforceBedrockLayer)
		{
			return true;
		}

		int bedrockY = ResolveBedrockGlobalY();

		// Keep generated bedrock immutable and prevent excavating/placing non-bedrock at bedrock depth.
		if (currentBlock == BlockType.Bedrock && requestedBlock != BlockType.Bedrock)
		{
			return false;
		}

		if (globalVoxel.Vertical <= bedrockY && requestedBlock != BlockType.Bedrock)
		{
			return false;
		}

		return true;
	}

	private int ResolveBedrockGlobalY()
	{
		if (UseDeterministicTerrainGeneration && TerrainSettings != null)
		{
			return TerrainSettings.BedrockY;
		}

		return FlatTerrainBedrockY;
	}

	private RingVoxelCoordinate LocalToGlobalVoxel(in RingChunkCoordinate coord, in Vector3I localVoxel)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coord);

		return new RingVoxelCoordinate(
			canonical.AroundRingChunk * VoxelConstants.ChunkSizeX + localVoxel.X,
			canonical.AcrossRingChunk * VoxelConstants.ChunkSizeZ + localVoxel.Z,
			canonical.VerticalChunk * VoxelConstants.ChunkSizeY + localVoxel.Y
		);
	}

	private RingChunkCoordinate WorldToChunkCoordinate(Vector3 worldPosition)
	{
		if (UseRingCurvedPlacement && UseHabitableRingAxisLayout)
		{
			return RingCoordMath.ToChunkCoordinate(
				WorldToCanonicalVoxelCoordinate(worldPosition),
				GetAroundRingVoxelLength());
		}

		Vector3 canonicalLocal = ToCanonicalLocalPosition(worldPosition);

		if (!UseRingCurvedPlacement)
		{
			int voxelX = Mathf.FloorToInt(canonicalLocal.X / VoxelConstants.VoxelSizeMeters);
			int voxelY = Mathf.FloorToInt(canonicalLocal.Y / VoxelConstants.VoxelSizeMeters);
			int voxelZ = Mathf.FloorToInt(canonicalLocal.Z / VoxelConstants.VoxelSizeMeters);

			RingChunkCoordinate linear = new RingChunkCoordinate(
				RingCoordMath.FloorDiv(voxelX, VoxelConstants.ChunkSizeX),
				RingCoordMath.FloorDiv(voxelZ, VoxelConstants.ChunkSizeZ),
				RingCoordMath.FloorDiv(voxelY, VoxelConstants.ChunkSizeY)
			);

			return CanonicalizeChunkCoordinate(linear);
		}

		Vector3 ringLocal = canonicalLocal - RingCenterLocalOffset;
		float ringRadius = Mathf.Max(1.0f, GetEffectiveRingRadiusMeters());

		float radialDistance = new Vector2(ringLocal.X, ringLocal.Z).Length();
		float angle = Mathf.Atan2(ringLocal.Z, ringLocal.X);
		if (angle < 0.0f)
		{
			angle += Mathf.Tau;
		}

		float aroundMeters = angle * ringRadius;
		float acrossMeters;
		float verticalMeters;
		if (UseHabitableRingAxisLayout)
		{
			acrossMeters = ringLocal.Y - RingAcrossBaseOffsetMeters;
			verticalMeters = ringRadius - radialDistance - RingVerticalBaseOffsetMeters;
		}
		else
		{
			acrossMeters = radialDistance - (ringRadius + RingAcrossBaseOffsetMeters);
			verticalMeters = ringLocal.Y - RingVerticalBaseOffsetMeters;
		}

		int aroundVoxel = Mathf.FloorToInt(aroundMeters / VoxelConstants.VoxelSizeMeters);
		int acrossVoxel = Mathf.FloorToInt(acrossMeters / VoxelConstants.VoxelSizeMeters);
		int verticalVoxel = Mathf.FloorToInt(verticalMeters / VoxelConstants.VoxelSizeMeters);

		var voxelCoordinate = new RingVoxelCoordinate(aroundVoxel, acrossVoxel, verticalVoxel);
		RingChunkCoordinate chunk = RingCoordMath.ToChunkCoordinate(voxelCoordinate, GetAroundRingVoxelLength());
		return CanonicalizeChunkCoordinate(chunk);
	}

	private RingVoxelCoordinate WorldToCanonicalVoxelCoordinate(Vector3 worldPosition)
	{
		RingCanonicalPosition canonical = WorldToCanonicalRingPosition64(worldPosition);
		double voxelSize = Math.Max(0.000001, VoxelConstants.VoxelSizeMeters);
		return new RingVoxelCoordinate(
			checked((long)Math.Floor(canonical.AroundMeters / voxelSize)),
			checked((int)Math.Floor(canonical.AcrossMeters / voxelSize)),
			checked((int)Math.Floor(canonical.VerticalMeters / voxelSize)));
	}

	private static bool TryParseChunkCoordKey(string key, out RingChunkCoordinate coordinate)
	{
		coordinate = default;
		if (string.IsNullOrWhiteSpace(key))
		{
			return false;
		}

		string[] parts = key.Split(',');
		if (parts.Length != 3)
		{
			return false;
		}

		bool aroundOk = long.TryParse(parts[0], out long around);
		bool acrossOk = int.TryParse(parts[1], out int across);
		bool verticalOk = int.TryParse(parts[2], out int vertical);

		if (!aroundOk || !acrossOk || !verticalOk)
		{
			return false;
		}

		coordinate = new RingChunkCoordinate(around, across, vertical);
		return true;
	}

	private static double GetNowSeconds()
	{
		return Time.GetTicksMsec() / 1000.0;
	}

	private Transform3D ChunkCoordinateToLocalTransform(in RingChunkCoordinate coordinate)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);

		float chunkWorldX = VoxelConstants.ChunkSizeX * VoxelConstants.VoxelSizeMeters;
		float chunkWorldY = VoxelConstants.ChunkSizeY * VoxelConstants.VoxelSizeMeters;
		float chunkWorldZ = VoxelConstants.ChunkSizeZ * VoxelConstants.VoxelSizeMeters;

		if (!UseRingCurvedPlacement)
		{
			Vector3 linearPosition = new Vector3(
				canonical.AroundRingChunk * chunkWorldX,
				canonical.VerticalChunk * chunkWorldY,
				canonical.AcrossRingChunk * chunkWorldZ);

			return new Transform3D(Basis.Identity, linearPosition);
		}

		float ringRadius = Mathf.Max(1.0f, GetEffectiveRingRadiusMeters());
		float aroundStartMeters = canonical.AroundRingChunk * chunkWorldX;
		float angle = aroundStartMeters / ringRadius;

		Vector3 outward = new Vector3(Mathf.Cos(angle), 0.0f, Mathf.Sin(angle));
		Vector3 tangent = new Vector3(-Mathf.Sin(angle), 0.0f, Mathf.Cos(angle));
		Vector3 widthAxis = Vector3.Up;

		float acrossStartMeters = RingAcrossBaseOffsetMeters + canonical.AcrossRingChunk * chunkWorldZ;
		float verticalStartMeters = RingVerticalBaseOffsetMeters + canonical.VerticalChunk * chunkWorldY;

		Vector3 origin;
		Basis basis;
		if (UseHabitableRingAxisLayout)
		{
			Vector3 inward = -outward;
			origin = RingCenterLocalOffset
				+ (outward * (ringRadius - verticalStartMeters))
				+ (widthAxis * acrossStartMeters);
			basis = new Basis(tangent, inward, widthAxis).Orthonormalized();
		}
		else
		{
			origin = RingCenterLocalOffset
				+ (outward * (ringRadius + acrossStartMeters))
				+ (widthAxis * verticalStartMeters);
			basis = new Basis(tangent, widthAxis, outward).Orthonormalized();
		}
		return new Transform3D(basis, origin);
	}

	private Transform3D ChunkCoordinateToWorldTransform(in RingChunkCoordinate coordinate)
	{
		RingLodGeometrySnapshot geometry = CaptureLodGeometrySnapshot();
		return ChunkCoordinateToWorldTransform(coordinate, geometry);
	}

	private Transform3D ChunkCoordinateToWorldTransform(
		in RingChunkCoordinate coordinate,
		in RingLodGeometrySnapshot geometry)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
		double chunkWorldX = VoxelConstants.ChunkSizeX * (double)VoxelConstants.VoxelSizeMeters;
		double chunkWorldY = VoxelConstants.ChunkSizeY * (double)VoxelConstants.VoxelSizeMeters;
		double chunkWorldZ = VoxelConstants.ChunkSizeZ * (double)VoxelConstants.VoxelSizeMeters;
		double around = canonical.AroundRingChunk * chunkWorldX;
		double vertical = canonical.VerticalChunk * chunkWorldY;
		double across = canonical.AcrossRingChunk * chunkWorldZ;
		Vector3 origin = geometry.CanonicalToWorld(around, vertical, across);

		double angle = around / geometry.RingRadiusMeters;
		Vector3 outward = new((float)Math.Cos(angle), 0.0f, (float)Math.Sin(angle));
		Vector3 tangent = new(-outward.Z, 0.0f, outward.X);
		Vector3 inward = -outward;
		Basis canonicalBasis = UseHabitableRingAxisLayout
			? new Basis(tangent, inward, Vector3.Up)
			: new Basis(tangent, Vector3.Up, outward);
		Basis worldBasis = (geometry.GridGlobalTransform.Basis * canonicalBasis).Orthonormalized();
		return new Transform3D(worldBasis, origin);
	}

	private void RefreshPrecisionSafeChunkTransforms()
	{
		if (!UseRingCurvedPlacement || !UseCameraRelativeTopLevelChunks) return;
		RingLodGeometrySnapshot geometry = CaptureLodGeometrySnapshot();
		foreach ((RingChunkCoordinate coordinate, VoxelChunkNode chunk) in _chunks)
			chunk.GlobalTransform = ChunkCoordinateToWorldTransform(coordinate, geometry);
	}

	private void ConfigureChunkCurvatureWarp(VoxelChunkNode chunk, in RingChunkCoordinate coordinate)
	{
		bool enableWarp = UseRingCurvedPlacement
			&& EnableCurvatureVertexWarp
			&& CurvatureVertexWarpStrength > 0.0001f;

		if (!enableWarp)
		{
			chunk.ConfigureRingCurvatureWarp(
				enabled: false,
				angularRadiusMeters: 1.0f,
				radialBaseMeters: 1.0f,
				arcStartMeters: 0.0f,
				warpStrength: 0.0f,
				radialDepthAxisY: UseHabitableRingAxisLayout);
			return;
		}

		float chunkWorldX = VoxelConstants.ChunkSizeX * VoxelConstants.VoxelSizeMeters;
		float chunkWorldZ = VoxelConstants.ChunkSizeZ * VoxelConstants.VoxelSizeMeters;

		float angularRadiusMeters = GetEffectiveRingRadiusMeters();
		float arcStartMeters = coordinate.AroundRingChunk * chunkWorldX;
		float radialBaseMeters = UseHabitableRingAxisLayout
			? angularRadiusMeters - (RingVerticalBaseOffsetMeters + coordinate.VerticalChunk * VoxelConstants.ChunkSizeY * VoxelConstants.VoxelSizeMeters)
			: angularRadiusMeters + RingAcrossBaseOffsetMeters + coordinate.AcrossRingChunk * chunkWorldZ;

		chunk.ConfigureRingCurvatureWarp(
			enabled: true,
			angularRadiusMeters: Mathf.Max(1.0f, angularRadiusMeters),
			radialBaseMeters: Mathf.Max(1.0f, radialBaseMeters),
			arcStartMeters: arcStartMeters,
			warpStrength: CurvatureVertexWarpStrength,
			radialDepthAxisY: UseHabitableRingAxisLayout);
	}

	private void ApplyCurvatureWarpToLoadedChunksAndRebuild()
	{
		InvalidateAllAsyncVoxelBuilds();
		foreach ((RingChunkCoordinate coordinate, VoxelChunkNode chunk) in _chunks)
		{
			ConfigureChunkCurvatureWarp(chunk, coordinate);
			chunk.Rebuild();
		}

		_queuedRebuildDeadlines.Clear();
		_seamGapDebugAccumulator = SeamGapDebugUpdateIntervalSeconds;
	}

	private void UpdateSeamGapDebugVisualization(double delta)
	{
		if (!EnableSeamGapDebugVisualization)
		{
			if (_seamGapDebugMeshInstance != null)
			{
				_seamGapDebugMeshInstance.Visible = false;
			}

			_seamGapDebugAccumulator = 0.0;
			return;
		}

		EnsureSeamGapDebugVisualizationResources();
		if (_seamGapDebugMesh == null || _seamGapDebugMeshInstance == null)
		{
			return;
		}

		_seamGapDebugAccumulator += delta;
		double interval = Math.Max(0.02, SeamGapDebugUpdateIntervalSeconds);
		if (_seamGapDebugAccumulator < interval)
		{
			return;
		}

		_seamGapDebugAccumulator = 0.0;

		_seamGapDebugMesh.ClearSurfaces();
		_seamGapDebugMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);

		float chunkWorldX = VoxelConstants.ChunkSizeX * VoxelConstants.VoxelSizeMeters;
		float chunkWorldY = VoxelConstants.ChunkSizeY * VoxelConstants.VoxelSizeMeters;
		float chunkWorldZ = VoxelConstants.ChunkSizeZ * VoxelConstants.VoxelSizeMeters;

		int sampleAcross = Mathf.Max(2, SeamGapDebugSamplesAcross);
		int sampleVertical = Mathf.Max(2, SeamGapDebugSamplesVertical);

		int seamSampleCount = 0;
		float seamGapSum = 0.0f;
		float seamGapMax = 0.0f;

		foreach ((RingChunkCoordinate coord, VoxelChunkNode chunk) in _chunks)
		{
			RingChunkCoordinate rightCoord = OffsetChunkCoordinate(coord, +1, 0, 0);
			if (!_chunks.TryGetValue(rightCoord, out VoxelChunkNode? rightChunk))
			{
				continue;
			}

			if (!ShouldDrawAroundSeamPair(coord, rightCoord))
			{
				continue;
			}

			for (int yIndex = 0; yIndex < sampleVertical; yIndex++)
			{
				float yT = sampleVertical == 1 ? 0.0f : (float)yIndex / (sampleVertical - 1);
				float localY = yT * chunkWorldY;

				for (int zIndex = 0; zIndex < sampleAcross; zIndex++)
				{
					float zT = sampleAcross == 1 ? 0.0f : (float)zIndex / (sampleAcross - 1);
					float localZ = zT * chunkWorldZ;

					Vector3 leftWorld = chunk.LinearLocalToWorldPoint(new Vector3(chunkWorldX, localY, localZ));
					Vector3 rightWorld = rightChunk.LinearLocalToWorldPoint(new Vector3(0.0f, localY, localZ));

					float gapMeters = leftWorld.DistanceTo(rightWorld);
					seamGapSum += gapMeters;
					seamGapMax = Mathf.Max(seamGapMax, gapMeters);
					seamSampleCount++;

					Color seamColor = GetSeamGapDebugColor(gapMeters);
					_seamGapDebugMesh.SurfaceSetColor(seamColor);
					_seamGapDebugMesh.SurfaceAddVertex(ToLocal(leftWorld));
					_seamGapDebugMesh.SurfaceAddVertex(ToLocal(rightWorld));
				}
			}
		}

		if (seamSampleCount > 0)
		{
			_seamGapDebugMesh.SurfaceEnd();
			_seamGapDebugMeshInstance.Visible = true;
		}
		else
		{
			// ImmediateMesh rejects an empty surface. This is normal while
			// streaming has not produced an eligible neighboring pair yet.
			_seamGapDebugMesh.ClearSurfaces();
			_seamGapDebugMeshInstance.Visible = false;
		}

		if (LogSeamGapDebugStats && seamSampleCount > 0)
		{
			float avgGap = seamGapSum / seamSampleCount;
			if (Mathf.Abs(seamGapMax - _lastLoggedSeamMaxGapMeters) >= 0.0005f)
			{
				GD.Print($"Seam gap debug: max={seamGapMax:0.000}m avg={avgGap:0.000}m samples={seamSampleCount}");
				_lastLoggedSeamMaxGapMeters = seamGapMax;
			}
		}
	}

	private void EnsureSeamGapDebugVisualizationResources()
	{
		if (_seamGapDebugMeshInstance == null || !IsInstanceValid(_seamGapDebugMeshInstance))
		{
			_seamGapDebugMeshInstance = GetNodeOrNull<MeshInstance3D>("SeamGapDebugMesh");
			if (_seamGapDebugMeshInstance == null)
			{
				_seamGapDebugMeshInstance = new MeshInstance3D { Name = "SeamGapDebugMesh" };
				AddChild(_seamGapDebugMeshInstance);
			}
		}

		if (_seamGapDebugMesh == null)
		{
			_seamGapDebugMesh = new ImmediateMesh();
			_seamGapDebugMeshInstance.Mesh = _seamGapDebugMesh;
		}

		if (_seamGapDebugMaterial == null)
		{
			_seamGapDebugMaterial = new StandardMaterial3D
			{
				VertexColorUseAsAlbedo = true,
				NoDepthTest = true
			};

			_seamGapDebugMeshInstance.MaterialOverride = _seamGapDebugMaterial;
		}
	}

	private bool ShouldDrawAroundSeamPair(in RingChunkCoordinate left, in RingChunkCoordinate right)
	{
		if (left.AcrossRingChunk != right.AcrossRingChunk || left.VerticalChunk != right.VerticalChunk)
		{
			return false;
		}

		if (!UseRingCurvedPlacement)
		{
			return left.AroundRingChunk < right.AroundRingChunk;
		}

		long aroundLen = GetAroundRingChunkLength();
		if (left.AroundRingChunk == aroundLen - 1 && right.AroundRingChunk == 0)
		{
			return true;
		}

		return left.AroundRingChunk < right.AroundRingChunk;
	}

	private Color GetSeamGapDebugColor(float gapMeters)
	{
		float scale = Mathf.Max(0.0001f, SeamGapDebugColorFullScaleMeters);
		float t = Mathf.Clamp(gapMeters / scale, 0.0f, 1.0f);
		Color low = new Color(0.15f, 0.95f, 0.25f, 0.9f);
		Color high = new Color(1.0f, 0.2f, 0.2f, 0.9f);
		return low.Lerp(high, t);
	}

	private long GetAroundRingChunkLength()
	{
		return _runtimeAroundRingChunkLength ?? Math.Max(1, AroundRingChunkLength);
	}

	private long GetAroundRingVoxelLength()
	{
		return GetAroundRingChunkLength() * VoxelConstants.ChunkSizeX;
	}

	private int GetLegacyAroundRingVoxelLength()
	{
		long aroundVoxelLength = (long)GetAroundRingChunkLength() * VoxelConstants.ChunkSizeX;
		if (aroundVoxelLength > int.MaxValue)
		{
			throw new InvalidOperationException(
				"The legacy heightfield edit projection cannot represent this ring circumference.");
		}

		return (int)aroundVoxelLength;
	}

	private float GetEffectiveRingRadiusMeters()
	{
		return (float)GetEffectiveRingRadiusMeters64();
	}

	private double GetEffectiveRingRadiusMeters64()
	{
		if (_runtimeRingRadiusMeters.HasValue)
		{
			return _runtimeRingRadiusMeters.Value;
		}

		if (!DeriveRingRadiusFromChunkLength)
		{
			return Math.Max(1.0, RingRadiusMeters);
		}

		double circumference = GetCanonicalRingCircumferenceMeters64();
		return Math.Max(1.0, circumference / Math.Tau);
	}

	private RingChunkCoordinate CanonicalizeChunkCoordinate(in RingChunkCoordinate coordinate)
	{
		if (!UseRingCurvedPlacement)
		{
			return coordinate;
		}

		return RingCoordMath.NormalizeChunkCoordinate(coordinate, GetAroundRingChunkLength());
	}

	private RingChunkCoordinate OffsetChunkCoordinate(in RingChunkCoordinate coordinate, int aroundDelta, int acrossDelta, int verticalDelta)
	{
		RingChunkCoordinate offset = new RingChunkCoordinate(
			coordinate.AroundRingChunk + aroundDelta,
			coordinate.AcrossRingChunk + acrossDelta,
			coordinate.VerticalChunk + verticalDelta);

		return CanonicalizeChunkCoordinate(offset);
	}

	private bool IsAcrossVoxelWithinRingBounds(int globalAcrossVoxel)
	{
		if (!UseHabitableRingAxisLayout || !EnforceRingAcrossBounds)
		{
			return true;
		}

		int widthVoxels = Mathf.Max(1, Mathf.RoundToInt(RingAcrossWidthMeters / VoxelConstants.VoxelSizeMeters));
		return globalAcrossVoxel >= 0 && globalAcrossVoxel < widthVoxels;
	}

	private bool IsAcrossVoxelInStructuralHull(int globalAcrossVoxel)
	{
		if (!UseHabitableRingAxisLayout || !EnforceRingAcrossBounds || !GenerateStructuralBedrockEdgeChunks)
		{
			return false;
		}

		int widthVoxels = Mathf.Max(1, Mathf.RoundToInt(RingAcrossWidthMeters / VoxelConstants.VoxelSizeMeters));
		int hullThickness = VoxelConstants.ChunkSizeZ;
		return (globalAcrossVoxel >= -hullThickness && globalAcrossVoxel < 0)
			|| (globalAcrossVoxel >= widthVoxels && globalAcrossVoxel < widthVoxels + hullThickness);
	}

	private bool IsAcrossVoxelWithinStructuralShell(int globalAcrossVoxel)
	{
		if (!UseHabitableRingAxisLayout || !EnforceRingAcrossBounds)
		{
			return false;
		}

		return IsAcrossVoxelWithinRingBounds(globalAcrossVoxel)
			|| IsAcrossVoxelInStructuralHull(globalAcrossVoxel);
	}

	private bool IsVerticalVoxelWithinStructuralUnderside(int globalVerticalVoxel)
	{
		if (!GenerateStructuralBedrockUndersideChunks)
		{
			return false;
		}

		return globalVerticalVoxel >= GetStructuralBottomGlobalY()
			&& globalVerticalVoxel < GetStructuralUndersideTopExclusiveGlobalY();
	}

	private bool IsStructuralBedrockVoxel(int globalVerticalVoxel, int globalAcrossVoxel)
	{
		if (IsAcrossVoxelInStructuralHull(globalAcrossVoxel)
			&& IsVerticalVoxelWithinStructuralHull(globalVerticalVoxel))
		{
			return true;
		}

		return IsAcrossVoxelWithinRingBounds(globalAcrossVoxel)
			&& IsVerticalVoxelWithinStructuralUnderside(globalVerticalVoxel);
	}

	private bool ChunkOverlapsStructuralUnderside(int start, int endExclusive)
	{
		if (!GenerateStructuralBedrockUndersideChunks)
		{
			return false;
		}

		return endExclusive > GetStructuralBottomGlobalY()
			&& start < GetStructuralUndersideTopExclusiveGlobalY();
	}

	private bool ChunkOverlapsStructuralShellAcross(int start, int endInclusive)
	{
		return IsAcrossVoxelWithinStructuralShell(start)
			|| IsAcrossVoxelWithinStructuralShell(endInclusive)
			|| (start < 0 && endInclusive >= 0)
			|| (start < Mathf.RoundToInt(RingAcrossWidthMeters / VoxelConstants.VoxelSizeMeters)
				&& endInclusive >= Mathf.RoundToInt(RingAcrossWidthMeters / VoxelConstants.VoxelSizeMeters));
	}

	private bool IsVerticalVoxelWithinStructuralHull(int globalVerticalVoxel)
	{
		int minimum = GetStructuralBottomGlobalY();
		int maximumExclusive = GetStructuralWallTopGlobalY() + 1;
		return globalVerticalVoxel >= minimum && globalVerticalVoxel < maximumExclusive;
	}

	private bool IsChunkWithinVerticalBounds(in RingChunkCoordinate coordinate)
	{
		int start = coordinate.VerticalChunk * VoxelConstants.ChunkSizeY;
		int endExclusive = start + VoxelConstants.ChunkSizeY;
		int acrossStart = coordinate.AcrossRingChunk * VoxelConstants.ChunkSizeZ;
		int acrossEnd = acrossStart + VoxelConstants.ChunkSizeZ - 1;
		bool normalTerrain = endExclusive > MinExcavationGlobalY
			&& start <= GetStructuralWallTopGlobalY();
		bool structuralWall = (IsAcrossVoxelInStructuralHull(acrossStart)
				|| IsAcrossVoxelInStructuralHull(acrossEnd))
			&& endExclusive > GetStructuralBottomGlobalY()
			&& start <= GetStructuralWallTopGlobalY();
		bool structuralUnderside = ChunkOverlapsStructuralUnderside(start, endExclusive)
			&& ChunkOverlapsStructuralShellAcross(acrossStart, acrossEnd);
		return normalTerrain || structuralWall || structuralUnderside;
	}

	private bool IsChunkWithinRingAcrossBounds(in RingChunkCoordinate coordinate)
	{
		if (!UseHabitableRingAxisLayout || !EnforceRingAcrossBounds)
		{
			return true;
		}

		int start = coordinate.AcrossRingChunk * VoxelConstants.ChunkSizeZ;
		int endExclusive = start + VoxelConstants.ChunkSizeZ;
		int widthVoxels = Mathf.Max(1, Mathf.RoundToInt(RingAcrossWidthMeters / VoxelConstants.VoxelSizeMeters));
		int hullThickness = GenerateStructuralBedrockEdgeChunks ? VoxelConstants.ChunkSizeZ : 0;
		return endExclusive > -hullThickness && start < widthVoxels + hullThickness;
	}

	private void InvalidateVirtualChunkClassification(in RingChunkCoordinate coordinate)
	{
		RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
		_knownEmptyStreamingChunks.Remove(canonical);
		_knownSolidStreamingChunks.Remove(canonical);
		CancelPendingAsyncVoxelBuild(canonical);
	}

	private Vector3 ToCanonicalWorldPosition(Vector3 worldPosition)
	{
		if (!EnableFloatingOriginBridge)
		{
			return worldPosition;
		}

		return worldPosition + _floatingOriginWorldOffset;
	}

	private Vector3 ToCanonicalLocalPosition(Vector3 worldPosition)
	{
		Vector3 canonicalWorld = ToCanonicalWorldPosition(worldPosition);
		return ToLocal(canonicalWorld);
	}

	private void UpdateFloatingOriginBridgeFromManagerMotion()
	{
		Vector3 currentGlobal = GlobalPosition;

		if (!_hasLastManagerGlobalPosition)
		{
			_lastManagerGlobalPosition = currentGlobal;
			_hasLastManagerGlobalPosition = true;
			return;
		}

		Vector3 managerDelta = currentGlobal - _lastManagerGlobalPosition;
		if (EnableFloatingOriginBridge && CompensateBridgeForManagerGlobalMotion)
		{
			if (!managerDelta.IsZeroApprox())
			{
				_floatingOriginWorldOffset += managerDelta;
			}
		}

		if (!managerDelta.IsZeroApprox()) RefreshPrecisionSafeChunkTransforms();

		_lastManagerGlobalPosition = currentGlobal;
	}

	private void ClearExistingChunks()
	{
		_streamingPlanGeneration++;
		_queuedStreamingPlan = null;
		InvalidateAllAsyncVoxelBuilds();
		_pendingChunkLoads.Clear();
		_pendingChunkLoadSet.Clear();
		_streamingDesiredVisual.Clear();
		_streamingDesiredCollision.Clear();
		OnClearExistingChunksPhase5();
		OnClearExistingChunksPhase7();

		_chunks.Clear();
		_voxelVisualCoverageRevision++;
		_knownEmptyStreamingChunks.Clear();
		_knownSolidStreamingChunks.Clear();
		_queuedRebuildDeadlines.Clear();
		_hasLastStreamingCenter = false;
		_streamingUpdateAccumulator = 0.0;
		_seamGapDebugAccumulator = 0.0;

		if (_seamGapDebugMesh != null)
		{
			_seamGapDebugMesh.ClearSurfaces();
		}

		if (_seamGapDebugMeshInstance != null)
		{
			_seamGapDebugMeshInstance.Visible = false;
		}

		// Pooled chunks remain parented to avoid expensive physics/render scene
		// re-registration. Drain them before the general child cleanup so each
		// node is freed exactly once during an explicit grid reset.
		while (_chunkPool.Count > 0)
		{
			VoxelChunkNode pooled = _chunkPool.Dequeue();
			if (!GodotObject.IsInstanceValid(pooled)) continue;
			if (pooled.GetParent() == this) RemoveChild(pooled);
			pooled.Free();
		}

		foreach (Node child in GetChildren())
		{
			if (child is not VoxelChunkNode)
			{
				continue;
			}

			RemoveChild(child);
			child.QueueFree();
		}

		PrewarmVoxelStreamingResources();
	}
}
