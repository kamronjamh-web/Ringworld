using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Camera-local, deterministic decoration pass. Decorations are MultiMesh
/// instances rather than chunk geometry, so they can be rebuilt independently
/// and never consume voxel memory or alter editable terrain.
/// </summary>
public partial class RingBiomeDecorationLayer : Node3D
{
	private readonly record struct BuildRequest(
		long AroundCell,
		long AcrossCell,
		RingCanonicalPosition Focus,
		RingLodGeometrySnapshot Geometry,
		Transform3D WorldToLayer,
		Vector3 PlayerWorldPosition,
		double RingWidthMeters,
		double CircumferenceMeters,
		float CellSizeMeters,
		float VisibilityRadiusMeters,
		float Lod1DistanceMeters,
		float Lod2DistanceMeters,
		float Lod3DistanceMeters,
		float MinimumLod5DistanceMeters,
		float MaximumDistanceMeters,
		int Candidates,
		int MaximumLod1,
		int MaximumLod2,
		int MaximumLod3,
		int MaximumLod4,
		int MaximumLod5,
		float ForestChance,
		float GrasslandChance,
		Plane[] CameraFrustum,
		float FrustumCellMarginMeters,
		float FrustumPaddingRadians,
		float BehindCameraFallbackRadiusMeters,
		Vector3 CameraForward,
		RingLodSurfaceEditSnapshot SurfaceEdits,
		ulong SurfaceRevision,
		float GroveDensity,
		float MaximumSlopeDegrees,
		float RootEmbedMeters,
		float SpatialTileMeters,
		int Lod1Variants,
		int Lod2Variants,
		int Lod3Variants,
		RingTerrainSampler Sampler);

	private readonly record struct CellCandidate(long AroundCell, long AcrossCell, TerrainSample Sample, float Priority);
	private readonly record struct SpatialBatchKey(long TileKey, int Variant);
	private readonly record struct TreePlacement(Transform3D Transform, int Variant);

	private readonly record struct BuildResult(
		long AroundCell,
		long AcrossCell, ulong SurfaceRevision,
		Dictionary<SpatialBatchKey, List<Transform3D>> Lod1,
		Dictionary<SpatialBatchKey, List<Transform3D>> Lod2,
		Dictionary<SpatialBatchKey, List<Transform3D>> Lod3,
		Dictionary<long, List<Transform3D>> Lod4,
		Dictionary<long, List<Transform3D>> Lod5,
		double WorkerMilliseconds);
	[Export] public bool Enabled { get; set; } = true;
	[Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
	[Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
	[Export] public NodePath Lod5Path { get; set; } = new("../Lod5Terrain32m");
	[Export] public NodePath SmoothTerrainPath { get; set; } = new("../SmoothTerrainGrid");
	// Thirty-metre cells with twelve candidates provide enough source density for
	// a dense grove after regional cover, slope, and water rejection.
	[Export(PropertyHint.Range, "20,250,5")] public float CellSizeMeters { get; set; } = 30.0f;
	[Export(PropertyHint.Range, "500,20000,100")] public float VisibilityRadiusMeters { get; set; } = 2600.0f;
	[ExportGroup("LOD distances")]
	[Export(PropertyHint.Range, "50,2000,10")] public float Lod1DistanceMeters { get; set; } = 240.0f;
	[Export(PropertyHint.Range, "100,4000,10")] public float Lod2DistanceMeters { get; set; } = 480.0f;
	[Export(PropertyHint.Range, "200,8000,10")] public float Lod3DistanceMeters { get; set; } = 960.0f;
	[Export(PropertyHint.Range, "0,20000,100")] public float MinimumLod5DistanceMeters { get; set; } = 1920.0f;
	[Export(PropertyHint.Range, "500,50000,100")] public float MaximumLod5DistanceMeters { get; set; } = 5500.0f;
	[ExportGroup("Billboard density")]
	[Export(PropertyHint.Range, "1,12,1")] public int CandidatesPerCell { get; set; } = 12;
	[Export(PropertyHint.Range, "256,65536,256")] public int MaximumTreeBillboards { get; set; } = 15000;
	[Export(PropertyHint.Range, "32,2048,32")] public int MaximumLod1Trees { get; set; } = 256;
	[Export(PropertyHint.Range, "64,4096,64")] public int MaximumLod2Trees { get; set; } = 640;
	[Export(PropertyHint.Range, "128,8192,128")] public int MaximumLod3Trees { get; set; } = 1800;
	[Export(PropertyHint.Range, "256,16384,256")] public int MaximumLod4Trees { get; set; } = 5200;
	[Export(PropertyHint.Range, "0,1,0.01")] public float ForestSpawnChance { get; set; } = 0.72f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float GrasslandSpawnChance { get; set; } = 0.20f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float GroveDensity { get; set; } = 0.70f;
	[Export(PropertyHint.Range, "0,60,1")] public float MaximumSlopeDegrees { get; set; } = 32.0f;
	[Export(PropertyHint.Range, "0.1,0.25,0.01")] public float RootEmbedMeters { get; set; } = 0.18f;
	[Export(PropertyHint.Range, "4,24,1")] public int SpatialTileCells { get; set; } = 8;
	[ExportGroup("Altitude visibility")]
	[Export(PropertyHint.Range, "1000,100000,1000")] public float MaximumViewerAltitudeAboveTerrainMeters { get; set; } = 12_000.0f;
	[ExportGroup("Sun shadow stabilization")]
	[Export] public bool StabilizeTreeSunShadowsDuringFastTravel { get; set; } = true;
	[Export(PropertyHint.Range, "0,2000,10")] public float TreeSunShadowSuspendSpeedMetersPerSecond { get; set; } = 250.0f;
	[Export(PropertyHint.Range, "0,250,5")] public float TreeSunShadowResumeSpeedMetersPerSecond { get; set; } = 25.0f;
	[Export(PropertyHint.Range, "0,5,0.1")] public float TreeSunShadowSettleHoldSeconds { get; set; } = 0.5f;
	[Export(PropertyHint.Range, "0,2000,10")] public float TreeSunShadowDistanceMeters { get; set; } = 480.0f;
	[Export(PropertyHint.Range, "0,1024,16")] public int MaximumTreeSunShadowBatches { get; set; } = 128;
	[ExportGroup("Streaming")]
	[Export] public bool UseCameraFrustumStreaming { get; set; } = true;
	[Export(PropertyHint.Range, "100,1500,25")] public float BehindCameraFallbackRadiusMeters { get; set; } = 400.0f;
	[Export(PropertyHint.Range, "2,45,1")] public float CameraTurnRefreshDegrees { get; set; } = 8.0f;
	[Export(PropertyHint.Range, "0,30,1")] public float FrustumPaddingDegrees { get; set; } = 11.0f;

	private VoxelChunkGridManager? _grid;
	private RingVoxelExplorerController? _explorer;
	private RingTerrainProxySector? _lod5;
	private SmoothTerrainGrid? _smoothTerrain;
	private MultiMeshInstance3D? _lod1Trunks;
	private MultiMeshInstance3D? _lod1Canopies;
	private MultiMeshInstance3D? _lod2Trunks;
	private MultiMeshInstance3D? _lod2Canopies;
	private MultiMeshInstance3D? _lod3Trunks;
	private MultiMeshInstance3D? _lod3Canopies;
	private MultiMeshInstance3D? _lod4Trees;
	private MultiMeshInstance3D? _lod5Trees;
	private MultiMeshInstance3D[]? _lod1AuthoredTemplates;
	private MultiMeshInstance3D[]? _lod2AuthoredTemplates;
	private MultiMeshInstance3D[]? _lod3AuthoredTemplates;
	private MultiMeshInstance3D[]? _lod4AuthoredTemplates;
	private readonly List<ShaderMaterial> _treeMaterials = new();
	private DirectionalLight3D? _treeSunLight;
	private Vector3 _lastTreeMaterialRingCenter = new(float.PositiveInfinity, 0.0f, 0.0f);
	private Vector3 _lastTreeMaterialSunDirection = new(float.PositiveInfinity, 0.0f, 0.0f);
	private TreeLightingConfiguration _lastTreeLightingConfiguration;
	private bool _treeLightingConfigurationValid;
	private readonly Dictionary<SpatialBatchKey, MultiMeshInstance3D> _lod1TrunkTiles = new();
	private readonly Dictionary<SpatialBatchKey, MultiMeshInstance3D> _lod1CanopyTiles = new();
	private readonly Dictionary<SpatialBatchKey, MultiMeshInstance3D> _lod2TrunkTiles = new();
	private readonly Dictionary<SpatialBatchKey, MultiMeshInstance3D> _lod2CanopyTiles = new();
	private readonly Dictionary<SpatialBatchKey, MultiMeshInstance3D> _lod3TrunkTiles = new();
	private readonly Dictionary<SpatialBatchKey, MultiMeshInstance3D> _lod3CanopyTiles = new();
	private readonly Dictionary<long, MultiMeshInstance3D> _lod4Tiles = new();
	private readonly Dictionary<long, MultiMeshInstance3D> _lod5Tiles = new();
	private readonly Dictionary<SpatialBatchKey, MultiMeshInstance3D> _vegetationLod4Tiles = new();
	private readonly Dictionary<SpatialBatchKey, MultiMeshInstance3D> _vegetationLod5Tiles = new();
	private long _lastAroundCell = long.MinValue;
	private long _lastAcrossCell = long.MinValue;
	private ulong _lastSurfaceRevision = ulong.MaxValue;
	private Vector3 _lastCameraForward = Vector3.Zero;
	private Task<BuildResult>? _pendingBuild;
	private BuildRequest? _queuedBuild;
	private bool _altitudeSuppressed;
	private readonly Dictionary<MultiMeshInstance3D, int> _treeSunShadowLods = new();
	private bool _treeSunShadowsSuppressed;
	private bool _treeSunShadowTravelActive;
	private double _treeSunShadowSettledSeconds;
	private bool _treeSunShadowPolicyDirty = true;
	private float _appliedTreeSunShadowDistance = float.NaN;
	private int _appliedTreeSunShadowBatchCap = -1;
	private bool? _appliedLowDetailTreeShadows;
	private bool? _appliedSimplifyDistantTreeMaterials;
	private long _treeSunShadowOrderSignature = long.MinValue;
	private static bool _frustumConventionValidated;

	// These are intentionally public diagnostics rather than log spam: the live
	// tuning/certification UI can sample them while a player traverses cells.
	public double LastWorkerBuildMilliseconds { get; private set; }
	public double LastInstallMilliseconds { get; private set; }
	public double MaxInstallMilliseconds { get; private set; }
	public int LastInstalledTransformCount { get; private set; }
	public bool TreeSunShadowsSuppressed => _treeSunShadowsSuppressed;
	public string TreeSunShadowSuppressionReason { get; private set; } = "active";
	public long TreeSunShadowTransitionCount { get; private set; }
	public int ActiveTreeSunShadowRendererCount => _treeSunShadowLods.Count(pair => GodotObject.IsInstanceValid(pair.Key)
		&& pair.Key.Visible && pair.Key.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off);
	public int ActiveGpuTreeSunShadowRendererCount => _gpuTreeOutputs.Values.Count(output => output.Renderer.Visible
		&& (output.Renderer.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off || HasGpuTreeShadowProxy(output)));
	public int ActiveGpuTreeShadowProxyCount => _gpuTreeShadowProxies.Values.Count(proxy => GodotObject.IsInstanceValid(proxy.Renderer) && proxy.Renderer.Visible);
	public int PendingGpuTreeShadowProxyCount => _gpuTreeShadowProxies.Values.Count(proxy => GodotObject.IsInstanceValid(proxy.Renderer) && !proxy.Ready);
	public bool UsingAuthoredTreeMeshes => _lod1AuthoredTemplates != null;
	private readonly record struct TreeLightingConfiguration(float DayStartDot, float DayFullDot,
		Vector3 NightTint, float NightDirectLightFactor, float DayDirectLightFactor);

	public override void _Ready()
	{
		ValidateFrustumConvention();
		_grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
		_explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
		_lod5 = GetNodeOrNull<RingTerrainProxySector>(Lod5Path);
		_smoothTerrain = GetNodeOrNull<SmoothTerrainGrid>(SmoothTerrainPath);
		if (!Enabled || _grid?.TerrainSampler == null || _explorer == null)
		{
            if (Enabled && UseStagedStartup) _startupResourceError = "Tree startup is missing its terrain sampler or explorer.";
			Visible = false;
			SetProcess(false);
			return;
		}
        if (UseStagedStartup) { _startupResources = PrepareStartupResources().GetEnumerator(); return; }
        InitializeStartupResources();
	}

    private void InitializeStartupResources()
    {
		// Imported records carry immutable species/variant identity and therefore
		// cannot use the obsolete camera-centred fallback builder.
		if (UseRegionalImportedForest) UsePersistentVegetationTiles = true;
		if (UseRegionalImportedForest)
		{
			var variants = ImportedTreeCatalog.Load();
			_lod1AuthoredTemplates = CreateImportedTemplates("ImportedForestLod0", variants, 0, true);
			_lod2AuthoredTemplates = CreateImportedTemplates("ImportedForestLod1", variants, 1, true);
			_lod3AuthoredTemplates = CreateImportedTemplates("ImportedForestLod2", variants, 2, true);
			_lod4AuthoredTemplates = CreateImportedTemplates("ImportedForestLod3", variants, 3, false);
		}
		else
		{
			Mesh?[] authored = LoadAuthoredTreeMeshes();
			if (Array.TrueForAll(authored, static mesh => mesh != null))
			{
			_lod1AuthoredTemplates = CreateAuthoredTemplates("BiomeTreesLod1Authored", Array.ConvertAll(authored, static mesh => mesh!), 0);
			_lod2AuthoredTemplates = CreateAuthoredTemplates("BiomeTreesLod2Authored", new[] { authored[2]!, authored[3]!, authored[5]! }, 1);
			_lod3AuthoredTemplates = CreateAuthoredTemplates("BiomeTreesLod3Authored", new[] { authored[2]! }, 2);
			}
			else
			{
			RingTreeMeshSet lod1 = RingTreeProceduralMeshFactory.Create(41031, 8, 18, addForks: true);
			RingTreeMeshSet lod2 = RingTreeProceduralMeshFactory.Create(27183, 6, 13, addForks: true);
			RingTreeMeshSet lod3 = RingTreeProceduralMeshFactory.Create(9187, 4, 9, addForks: false);
			_lod1Trunks = CreateInstances("BiomeTreesLod1Trunks", lod1.Trunk, CreateTrunkMaterial(), true, 0);
			_lod1Canopies = CreateInstances("BiomeTreesLod1Canopies", lod1.Canopy, CreateCanopyMaterial(new Color(.045f, .20f, .065f, 1)), true, 0);
			_lod2Trunks = CreateInstances("BiomeTreesLod2Trunks", lod2.Trunk, CreateTrunkMaterial(), true, 1);
			_lod2Canopies = CreateInstances("BiomeTreesLod2Canopies", lod2.Canopy, CreateCanopyMaterial(new Color(.055f, .22f, .075f, 1)), true, 1);
			_lod3Trunks = CreateInstances("BiomeTreesLod3Trunks", lod3.Trunk, CreateTrunkMaterial(), true, 2);
			_lod3Canopies = CreateInstances("BiomeTreesLod3Canopies", lod3.Canopy, CreateCanopyMaterial(new Color(.07f, .27f, .09f, 1)), true, 2);
			}
		}
		_lod4Trees = CreateInstances("BiomeTreesLod4", new QuadMesh { Size = Vector2.One }, CreateBillboardMaterial(new Color(.08f, .27f, .10f, 1)), false);
		_lod5Trees = CreateInstances("BiomeTreesLod5", new QuadMesh { Size = Vector2.One }, CreateBillboardMaterial(new Color(.08f, .27f, .10f, 1)), false);
		if (UsePersistentVegetationTiles) QueueTileStreaming(true);
		else QueueRebuild(true);
	}

	public override void _Process(double delta)
	{
		using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Trees update (inclusive)");
        if (UseStagedStartup && !StartupComplete)
        {
            ServiceStartupResources();
            return;
        }
		// This must precede streaming's movement early-outs: celestial state and
		// floating-origin canonical center change while a player stands still.
		UpdateTreeLightingMaterials();
		UpdateDistantTreeMaterialPolicy();
		ServiceGpuTreeShadowMirrors();
		// Trees are independently streamed.  The terrain proxy is intentionally
		// suspended while detailed terrain takes over, so using its StreamingActive
		// flag here removed every tree LOD during a normal proxy handoff. Suppress
		// them by actual AGL instead, which also prevents ground vegetation from
		// lingering during space flight.
		bool shouldShow = _explorer == null || _explorer.CurrentAltitudeAboveTerrainMeters <= MaximumViewerAltitudeAboveTerrainMeters;
		UpdateTreeSunShadowPolicy(delta, shouldShow);
		if (!shouldShow)
		{
			if (!_altitudeSuppressed && UsePersistentVegetationTiles)
				SuspendVegetationStreaming();
			ClearImportedForestCollisions();
			_altitudeSuppressed = true;
			Visible = false;
			return;
		}
		if (_altitudeSuppressed)
		{
			_altitudeSuppressed = false;
			_lastAroundCell = long.MinValue;
			_lastAcrossCell = long.MinValue;
		}
		Visible = true;
		if (UsePersistentVegetationTiles)
		{
			using (RingPerformanceTelemetry.MeasureCpu("Trees queue"))
				QueueTileStreaming(false);
			CompleteTileStreaming();
			UpdateImportedForestCollisions();
		}
		else
		{
			QueueRebuild(false);
			CompleteBuildIfReady();
		}
	}

	private static readonly string[] AuthoredTreePaths =
	{
		"res://resources/trees/kaykit_forest/Tree_1_A_Color1.gltf", "res://resources/trees/kaykit_forest/Tree_1_B_Color1.gltf",
		"res://resources/trees/kaykit_forest/Tree_2_A_Color1.gltf", "res://resources/trees/kaykit_forest/Tree_2_C_Color1.gltf",
		"res://resources/trees/kaykit_forest/Tree_3_A_Color1.gltf", "res://resources/trees/kaykit_forest/Tree_4_B_Color1.gltf"
	};

	private static Mesh?[] LoadAuthoredTreeMeshes()
	{
		var meshes = new Mesh?[AuthoredTreePaths.Length];
		for (int i = 0; i < meshes.Length; i++) meshes[i] = KayKitTreeMeshLoader.LoadNormalizedMesh(AuthoredTreePaths[i]);
		return meshes;
	}

	private MultiMeshInstance3D[] CreateAuthoredTemplates(string prefix, IReadOnlyList<Mesh> meshes, int shadowLod)
	{
		var templates = new MultiMeshInstance3D[meshes.Count];
		ShaderMaterial material = CreateTreeMaterial(
			Colors.White,
			useAuthoredPalette: true);
		for (int i = 0; i < templates.Length; i++)
			templates[i] = CreateInstances($"{prefix}_{i}", meshes[i], material, true, shadowLod);
		return templates;
	}

	private MultiMeshInstance3D[] CreateImportedTemplates(string prefix, IReadOnlyList<ImportedTreeVariant> variants, int lod, bool castsShadow)
	{
		var templates = new MultiMeshInstance3D[variants.Count];
		for (int i = 0; i < templates.Length; i++)
		{
			templates[i] = CreateInstances($"{prefix}_{variants[i].Index}", variants[i].Lods[lod], null, castsShadow, castsShadow ? lod : -1);
			foreach (ShaderMaterial material in variants[i].Materials)
			{
				RegisterTreeLightingMaterial(material);
			}
		}
		return templates;
	}

	private MultiMeshInstance3D CreateInstances(string name, Mesh mesh, Material? material, bool castsShadow, int shadowLod = -1)
	{
		var multiMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh };
		var instance = new MultiMeshInstance3D
		{
			Name = name, Multimesh = multiMesh, MaterialOverride = material,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			// The MultiMesh bounds already include its transforms; only tree height
			// and a little worker/build drift need expanding here.
			ExtraCullMargin = CellSizeMeters + 32.0f
		};
		AddChild(instance);
		RegisterTreeSunShadowRenderer(instance, castsShadow ? shadowLod : -1);
		return instance;
	}

	private void RegisterTreeSunShadowRenderer(MultiMeshInstance3D renderer, int lod)
	{
		if (lod >= 0) _treeSunShadowLods[renderer] = lod;
		ApplyTreeSunShadowState(renderer);
	}

	private int TreeSunShadowLodOf(MultiMeshInstance3D renderer)
		=> _treeSunShadowLods.TryGetValue(renderer, out int lod) ? lod : -1;
	private void UnregisterTreeSunShadowRenderer(MultiMeshInstance3D renderer)
	{
		_treeSunShadowLods.Remove(renderer);
		MarkTreeSunShadowPolicyDirty();
	}

	private bool AllowsTreeSunShadowLod(int lod)
	{
		float distance = Mathf.Clamp(TreeSunShadowDistanceMeters, 0.0f, Mathf.Max(0.0f, Lod3DistanceMeters));
		if (EffectiveCompactTreeLodProfile)
		{
			CompactTreeFadeBounds(out float nearStart, out _, out _, out _);
			return lod switch
			{
				0 => distance > 0.0f,
				// Compact LOD2 begins while LOD0 fades, rather than at the old
				// authored-LOD1 boundary. Its shadow can therefore remain selected.
				2 => distance > nearStart,
				_ => false
			};
		}
		return lod switch
		{
			0 => distance > 0.0f,
			1 => distance > Lod1DistanceMeters,
			2 => distance > Lod2DistanceMeters,
			_ => false
		};
	}

	private void ApplyTreeSunShadowState(MultiMeshInstance3D renderer)
	{
		if (!_treeSunShadowLods.TryGetValue(renderer, out int lod)) return;
		renderer.CastShadow = !_treeSunShadowsSuppressed && AllowsTreeSunShadowLod(lod)
			? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
	}

	private void ApplyTreeSunShadowState()
	{
		foreach ((MultiMeshInstance3D renderer, _) in _treeSunShadowLods.ToArray())
		{
			if (!GodotObject.IsInstanceValid(renderer)) { _treeSunShadowLods.Remove(renderer); continue; }
			ApplyTreeSunShadowState(renderer);
		}
		// The GPU output list is already distance-sorted for gradual installation.
		// Keep the directional atlas bounded even after a large traversal fills every
		// visual tree batch. CPU fallback remains conservative through the distance
		// policy above; GPU is the high-cardinality production path.
		int remaining = Mathf.Max(0, MaximumTreeSunShadowBatches);
		var selectedGpu = new HashSet<MultiMeshInstance3D>();
		if (!_treeSunShadowsSuppressed && remaining > 0)
		{
			foreach (var item in _gpuTreeOrder.OrderBy(item => item.Distance))
			{
				if (item.Key.Lod >= 3 || !AllowsTreeSunShadowLod(item.Key.Lod)) continue;
				if (_gpuTreeOutputs.TryGetValue(item.Key, out GpuTreeOutput? output) && selectedGpu.Add(output.Renderer) && --remaining == 0) break;
			}
		}
		foreach ((GpuOutputKey key, GpuTreeOutput output) in _gpuTreeOutputs)
			ApplyGpuTreeSunShadowChoice(key, output, selectedGpu.Contains(output.Renderer));
		_appliedTreeSunShadowDistance = TreeSunShadowDistanceMeters;
		_appliedTreeSunShadowBatchCap = MaximumTreeSunShadowBatches;
		_treeSunShadowPolicyDirty = false;
	}

	private void MarkTreeSunShadowPolicyDirty() => _treeSunShadowPolicyDirty = true;
	private void UpdateGpuTreeSunShadowOrderSignature(long signature)
	{
		if (_treeSunShadowOrderSignature == signature) return;
		_treeSunShadowOrderSignature = signature;
		// While suppressed every output is already shadow-off. Remember the latest
		// order without walking the renderer set on every fast-travel frame; the
		// suppression transition will apply this order once shadows resume.
		if (!_treeSunShadowsSuppressed) MarkTreeSunShadowPolicyDirty();
	}

	private void UpdateTreeSunShadowPolicy(double delta, bool shouldShow, float? fixtureSpeed = null, bool? fixtureSettled = null)
	{
		float speed = fixtureSpeed ?? _explorer?.ActualTravelSpeedMetersPerSecond ?? 0.0f;
		float suspend = Mathf.Max(1.0f, TreeSunShadowSuspendSpeedMetersPerSecond);
		float resume = Mathf.Clamp(TreeSunShadowResumeSpeedMetersPerSecond, 0.0f, suspend);
		bool fast = StabilizeTreeSunShadowsDuringFastTravel && speed >= suspend;
		if (!StabilizeTreeSunShadowsDuringFastTravel) _treeSunShadowTravelActive = false;
		if (fast) _treeSunShadowTravelActive = true;
		bool settled = fixtureSettled ?? (VegetationPendingTiles == 0 && VegetationPendingRenderUploads == 0
			&& (!UseGpuCulling || (!GpuTreeCullingActive ? !_vegetationRenderDirty : GpuTreeCullingSettled)));
		bool suppress = !shouldShow || fast;
		string reason = !shouldShow ? "altitude" : fast ? $"travel {speed:F0} m/s" : "active";
		if (!suppress && _treeSunShadowsSuppressed && _treeSunShadowTravelActive)
		{
			if (speed > resume) { suppress = true; reason = $"travel cooldown {speed:F0} m/s"; _treeSunShadowSettledSeconds = 0; }
			else if (!settled) { suppress = true; reason = "vegetation settling"; _treeSunShadowSettledSeconds = 0; }
			else if ((_treeSunShadowSettledSeconds += Math.Max(0.0, delta)) < Mathf.Max(0.0f, TreeSunShadowSettleHoldSeconds))
			{ suppress = true; reason = "settle hold"; }
			else _treeSunShadowTravelActive = false;
		}
		else if (suppress) _treeSunShadowSettledSeconds = 0;
		if (_treeSunShadowsSuppressed != suppress)
		{
			_treeSunShadowsSuppressed = suppress;
			TreeSunShadowTransitionCount++;
			MarkTreeSunShadowPolicyDirty();
		}
		if (_appliedTreeSunShadowDistance != TreeSunShadowDistanceMeters || _appliedTreeSunShadowBatchCap != MaximumTreeSunShadowBatches
			|| _appliedLowDetailTreeShadows != UseLowDetailTreeShadows)
			MarkTreeSunShadowPolicyDirty();
		if (_treeSunShadowPolicyDirty)
			ApplyTreeSunShadowState();
		TreeSunShadowSuppressionReason = reason;
		_appliedLowDetailTreeShadows = UseLowDetailTreeShadows;
	}

	private void UpdateDistantTreeMaterialPolicy()
	{
		if (_appliedSimplifyDistantTreeMaterials == SimplifyDistantTreeMaterials) return;
		if (!UsingAuthoredTreeMeshes) return;
		foreach (ImportedTreeVariant variant in ImportedTreeCatalog.Load())
		{
			// The clone keeps GPU LOD2's material state independent of the backend
			// output mesh. It is created even while simplified shading is disabled so
			// the live toggle never swaps a mesh whose indirect RID is in use.
			foreach (Mesh mesh in new[] { variant.Lods[2], ImportedTreeRenderMaterials.GetSimplifiedMesh(variant.Lods[2]), variant.Lods[3] })
				foreach (ShaderMaterial material in ImportedTreeRenderMaterials.ShaderMaterials(mesh))
				{
					material.SetShaderParameter("simplified_tree_shading", SimplifyDistantTreeMaterials);
					RegisterTreeLightingMaterial(material);
				}
		}
		if (_sharedFarTreeMaterial != null && GodotObject.IsInstanceValid(_sharedFarTreeMaterial))
			_sharedFarTreeMaterial.SetShaderParameter("simplified_tree_shading", SimplifyDistantTreeMaterials);
		_appliedSimplifyDistantTreeMaterials = SimplifyDistantTreeMaterials;
	}

	internal void StepTreeSunShadowPolicyFixture(float speedMetersPerSecond, bool settled, double delta)
		=> UpdateTreeSunShadowPolicy(delta, true, speedMetersPerSecond, settled);
	internal int GpuTreeFixtureSunShadowRendererCount()
		=> _gpuTreeOutputs.Values.Count(output => output.Renderer.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off);
	internal bool GpuTreeFixtureLodCastsShadow(int lod)
		=> _gpuTreeOutputs.Where(pair => pair.Key.Lod == lod).Any(pair => pair.Value.Renderer.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off);

	private ShaderMaterial CreateTrunkMaterial() =>
		CreateTreeMaterial(new Color(.19f, .105f, .045f, 1), useAuthoredPalette: false);

	private ShaderMaterial CreateCanopyMaterial(Color color)
	{
		ShaderMaterial material = CreateTreeMaterial(color, useAuthoredPalette: false);
		material.SetShaderParameter("tree_foliage_mode", 1);
		material.SetShaderParameter("tree_foliage_attachments", true);
		return material;
	}

	private ShaderMaterial CreateBillboardMaterial(Color color)
	{
		ShaderMaterial material = CreateTreeMaterial(color, useAuthoredPalette: false);
		material.SetShaderParameter("vegetation_wind_enabled", false);
		return material;
	}

	private ShaderMaterial CreateTreeMaterial(Color color, bool useAuthoredPalette)
	{
		var material = new ShaderMaterial
		{
			Shader = GD.Load<Shader>("res://materials/ring_tree.gdshader")
		};
		material.SetShaderParameter("tree_tint", new Vector3(color.R, color.G, color.B));
		material.SetShaderParameter("use_albedo_texture", useAuthoredPalette);
		material.SetShaderParameter("tree_foliage_mode", useAuthoredPalette ? 2 : 0);
		if (useAuthoredPalette)
		{
			material.SetShaderParameter(
				"tree_albedo_texture",
				GD.Load<Texture2D>("res://resources/trees/kaykit_forest/forest_texture.png"));
		}
		RegisterTreeLightingMaterial(material);
		return material;
	}

	private void UpdateTreeLightingMaterials()
	{
		if (_grid == null || _treeMaterials.Count == 0) return;
		ResolveTreeSunLight();
		Vector3 ringCenter = _grid.GetCanonicalRingCenterWorld();
		Vector3 sunDirection = _treeSunLight?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
		var configuration = new TreeLightingConfiguration(_grid.RingDayStartDot,
			Mathf.Max(_grid.RingDayStartDot + .001f, _grid.RingDayFullDot),
			new Vector3(_grid.RingNightTint.R, _grid.RingNightTint.G, _grid.RingNightTint.B),
			_grid.RingNightDirectLightFactor, _grid.RingDayDirectLightFactor);
		// Exact comparisons retain small floating-origin shifts even when the
		// canonical ring center has multi-million-metre components.
		bool centerChanged = _lastTreeMaterialRingCenter != ringCenter;
		bool sunChanged = _lastTreeMaterialSunDirection != sunDirection;
		bool configurationChanged = !_treeLightingConfigurationValid || configuration != _lastTreeLightingConfiguration;
		if (centerChanged || sunChanged || configurationChanged)
			foreach (ShaderMaterial material in _treeMaterials)
				ConfigureTreeLightingMaterial(material, centerChanged, sunChanged, configurationChanged);
		// The finite-cylinder air path is camera-relative, so publish it every
		// frame even while celestial lighting and profile values are unchanged.
		UpdateTreeAtmosphereRayFrame(sunDirection);
		_lastTreeMaterialRingCenter = ringCenter;
		_lastTreeMaterialSunDirection = sunDirection;
		_lastTreeLightingConfiguration = configuration;
		_treeLightingConfigurationValid = true;
	}

	private void ResolveTreeSunLight()
	{
		if (_grid != null && (_treeSunLight == null || !GodotObject.IsInstanceValid(_treeSunLight)))
			_treeSunLight = _grid.GetNodeOrNull<DirectionalLight3D>(_grid.RingSunLightPath);
	}

	private void ConfigureTreeLightingMaterial(ShaderMaterial material, bool centerChanged, bool sunChanged, bool configurationChanged)
	{
		if (_grid == null) return;
		ResolveTreeSunLight();
		Vector3 ringCenter = _grid.GetCanonicalRingCenterWorld();
		Vector3 sunDirection = _treeSunLight?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
		if (centerChanged) material.SetShaderParameter("ring_center", ringCenter);
		if (sunChanged) material.SetShaderParameter("sun_direction", sunDirection);
		if (!configurationChanged) return;
		material.SetShaderParameter("day_start_dot", _grid.RingDayStartDot);
		material.SetShaderParameter("day_full_dot", Mathf.Max(_grid.RingDayStartDot + .001f, _grid.RingDayFullDot));
		material.SetShaderParameter("night_tint", new Vector3(_grid.RingNightTint.R, _grid.RingNightTint.G, _grid.RingNightTint.B));
		material.SetShaderParameter("night_direct_light_factor", _grid.RingNightDirectLightFactor);
		material.SetShaderParameter("day_direct_light_factor", _grid.RingDayDirectLightFactor);
	}

	private void QueueRebuild(bool force)
	{
		if (_grid?.TerrainSampler is not RingTerrainSampler sampler || _explorer == null) return;
		float cell = Mathf.Max(20, CellSizeMeters);
		RingCanonicalPosition focus = _explorer.CanonicalPosition;
		long aroundCell = (long)Math.Floor(focus.AroundMeters / cell);
		long acrossCell = (long)Math.Floor(focus.AcrossMeters / cell);
		Camera3D? camera = GetViewport().GetCamera3D();
		Vector3 cameraForward = (camera != null ? -camera.GlobalTransform.Basis.Z : -_explorer.GlobalTransform.Basis.Z).Normalized();
		// A modest turn refreshes the candidate set, but tiny mouse movement does
		// not continually supersede the worker. Existing trees remain installed
		// until the new set has completed.
		float turnThreshold = Mathf.Cos(Mathf.DegToRad(Mathf.Clamp(CameraTurnRefreshDegrees, 2.0f, 45.0f)));
		bool cameraTurned = _lastCameraForward.LengthSquared() > 0.01f
			&& _lastCameraForward.Dot(cameraForward) < turnThreshold;
		// This is captured before the movement early-out. Edits must refresh trees
		// even while the player remains in one decoration cell.
		RingLodSurfaceEditSnapshot surfaceEdits = _smoothTerrain?.CaptureLodSurfaceEditSnapshot()
			?? _grid.CaptureLodSurfaceEditSnapshot();
		bool surfaceChanged = surfaceEdits.Version != _lastSurfaceRevision;
		if (!force && !surfaceChanged && !cameraTurned && aroundCell == _lastAroundCell && acrossCell == _lastAcrossCell) return;
		_lastAroundCell = aroundCell;
		_lastAcrossCell = acrossCell;
		_lastSurfaceRevision = surfaceEdits.Version;
		Plane[] frustum = Array.Empty<Plane>();
		if (UseCameraFrustumStreaming && camera != null)
		{
			Godot.Collections.Array<Plane> cameraFrustum = camera.GetFrustum();
			frustum = new Plane[cameraFrustum.Count];
			for (int i = 0; i < cameraFrustum.Count; i++) frustum[i] = cameraFrustum[i];
		}
		_lastCameraForward = cameraForward;
		float lod1Distance = Mathf.Max(1.0f, Lod1DistanceMeters);
		float lod2Distance = Mathf.Max(lod1Distance, Lod2DistanceMeters);
		float lod3Distance = Mathf.Max(lod2Distance, Lod3DistanceMeters);
		float minimumLod5Distance = Mathf.Clamp(MinimumLod5DistanceMeters, lod3Distance, Mathf.Max(lod3Distance, MaximumLod5DistanceMeters));
		float maximumLod5Distance = Mathf.Max(minimumLod5Distance, MaximumLod5DistanceMeters);
		// The immutable projection was captured on the main thread above; never
		// touch scene nodes from the worker.
		var request = new BuildRequest(
			aroundCell, acrossCell, focus, _grid.CaptureLodGeometrySnapshot(),
			GlobalTransform.AffineInverse(), _explorer.GlobalPosition,
			_grid.RingAcrossWidthMeters, _grid.GetCanonicalRingCircumferenceMeters64(), cell, VisibilityRadiusMeters,
			lod1Distance, lod2Distance, lod3Distance,
			minimumLod5Distance, maximumLod5Distance, Mathf.Clamp(CandidatesPerCell, 1, 12),
			Mathf.Clamp(MaximumLod1Trees, 32, 2_048), Mathf.Clamp(MaximumLod2Trees, 64, 4_096),
			Mathf.Clamp(MaximumLod3Trees, 128, 8_192), Mathf.Clamp(MaximumLod4Trees, 256, 16_384),
			Mathf.Clamp(MaximumTreeBillboards, 256, 65_536), ForestSpawnChance, GrasslandSpawnChance,
			frustum, cell * .75f + 18.0f, Mathf.DegToRad(Mathf.Clamp(FrustumPaddingDegrees, 0.0f, 30.0f)),
			Mathf.Clamp(BehindCameraFallbackRadiusMeters, cell, VisibilityRadiusMeters), cameraForward,
			surfaceEdits, surfaceEdits.Version, Mathf.Clamp(GroveDensity, 0.0f, 1.0f),
			Mathf.Clamp(MaximumSlopeDegrees, 0.0f, 60.0f), Mathf.Clamp(RootEmbedMeters, .1f, .25f),
			cell * Mathf.Clamp(SpatialTileCells, 4, 24),
			_lod1AuthoredTemplates?.Length ?? 1, _lod2AuthoredTemplates?.Length ?? 1, _lod3AuthoredTemplates?.Length ?? 1,
			sampler.CreateIndependentCopy());
		if (_pendingBuild != null)
		{
			if (_queuedBuild is { } previous) previous.Sampler.Dispose();
			_queuedBuild = request;
			return;
		}
		StartBuild(request);
	}

	private void StartBuild(BuildRequest request)
	{
		_pendingBuild = Task.Run(() => BuildTransforms(request));
	}

	private void CompleteBuildIfReady()
	{
		if (_pendingBuild == null || !_pendingBuild.IsCompleted) return;
		Task<BuildResult> completed = _pendingBuild;
		_pendingBuild = null;
		// A newer camera/movement snapshot is queued: retain the loaded set and
		// install only that newer result. This prevents a turn from briefly
		// replacing nearby behind-camera vegetation with an obsolete candidate set.
		bool surfaceStillCurrent = false;
		if (!completed.IsFaulted && !completed.IsCanceled)
		{
			ulong currentSurfaceRevision = (_smoothTerrain?.CaptureLodSurfaceEditSnapshot()
				?? _grid!.CaptureLodSurfaceEditSnapshot()).Version;
			surfaceStillCurrent = currentSurfaceRevision == completed.Result.SurfaceRevision;
		}
		if (!completed.IsFaulted && !completed.IsCanceled && _queuedBuild == null && surfaceStillCurrent
			&& _lod4Trees != null && _lod5Trees != null)
		{
			using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Trees install");
			BuildResult result = completed.Result;
			LastWorkerBuildMilliseconds = result.WorkerMilliseconds;
			// A previous version made one managed-to-native call per transform here.
			// At ground level a 5.5 km refresh can install tens of thousands of
			// transforms (and writes the first three bands twice for trunk/canopy),
			// producing a visible main-thread hitch. Each active spatial tile gets one
			// bulk buffer submission per mesh part, retaining culling without a
			// managed-to-native transition for every tree.
			ulong installStart = Time.GetTicksUsec();
			if (UsingAuthoredTreeMeshes)
			{
				InstallAuthoredTiles(_lod1AuthoredTemplates!, _lod1TrunkTiles, result.Lod1);
				InstallAuthoredTiles(_lod2AuthoredTemplates!, _lod2TrunkTiles, result.Lod2);
				InstallAuthoredTiles(_lod3AuthoredTemplates!, _lod3TrunkTiles, result.Lod3);
			}
			else
			{
				InstallVariantTiles(new[] { _lod1Trunks! }, _lod1TrunkTiles, result.Lod1); InstallVariantTiles(new[] { _lod1Canopies! }, _lod1CanopyTiles, result.Lod1);
				InstallVariantTiles(new[] { _lod2Trunks! }, _lod2TrunkTiles, result.Lod2); InstallVariantTiles(new[] { _lod2Canopies! }, _lod2CanopyTiles, result.Lod2);
				InstallVariantTiles(new[] { _lod3Trunks! }, _lod3TrunkTiles, result.Lod3); InstallVariantTiles(new[] { _lod3Canopies! }, _lod3CanopyTiles, result.Lod3);
			}
			InstallTiles(_lod4Trees, _lod4Tiles, result.Lod4); InstallTiles(_lod5Trees, _lod5Tiles, result.Lod5);
			int closeParts = UsingAuthoredTreeMeshes ? 1 : 2;
			LastInstalledTransformCount = CountTransforms(result.Lod1) * closeParts + CountTransforms(result.Lod2) * closeParts
				+ CountTransforms(result.Lod3) * closeParts + CountTransforms(result.Lod4) + CountTransforms(result.Lod5);
			LastInstallMilliseconds = (Time.GetTicksUsec() - installStart) / 1000.0;
			MaxInstallMilliseconds = Math.Max(MaxInstallMilliseconds, LastInstallMilliseconds);
		}
		if (!surfaceStillCurrent && _queuedBuild == null)
		{
			// Do not install a result for an obsolete edit projection. Queue the
			// current immutable snapshot immediately instead of waiting for movement.
			QueueRebuild(true);
		}
		if (_queuedBuild is { } queued)
		{
			_queuedBuild = null;
			StartBuild(queued);
		}
	}

	private static BuildResult BuildTransforms(BuildRequest request)
	{
		using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Trees build", worker: true);
		Stopwatch stopwatch = Stopwatch.StartNew();
		using RingTerrainSampler sampler = request.Sampler;
		// Billboard LOD reaches beyond the detailed vegetation radius.  Limiting
		// the cell scan to VisibilityRadiusMeters starved LOD5 whenever its
		// configured maximum was larger (the default 5.5 km band was only scanned
		// to 2.6 km), which made the distant tree LOD appear to be removed.
		int radius = (int)Math.Ceiling(Mathf.Max(request.VisibilityRadiusMeters, request.MaximumDistanceMeters) / request.CellSizeMeters);
		var lod1 = new List<TreePlacement>(request.MaximumLod1);
		var lod2 = new List<TreePlacement>(request.MaximumLod2);
		var lod3 = new List<TreePlacement>(request.MaximumLod3);
		var lod4 = new List<Transform3D>(request.MaximumLod4);
		var lod5 = new List<Transform3D>(request.MaximumLod5);
		var cells = new List<CellCandidate>();
		for (long z = request.AcrossCell - radius; z <= request.AcrossCell + radius && (lod1.Count < request.MaximumLod1 || lod2.Count < request.MaximumLod2 || lod3.Count < request.MaximumLod3 || lod4.Count < request.MaximumLod4 || lod5.Count < request.MaximumLod5); z++)
		for (long x = request.AroundCell - radius; x <= request.AroundCell + radius && (lod1.Count < request.MaximumLod1 || lod2.Count < request.MaximumLod2 || lod3.Count < request.MaximumLod3 || lod4.Count < request.MaximumLod4 || lod5.Count < request.MaximumLod5); x++)
		{
			double sampleAround = (x + .5) * request.CellSizeMeters;
			double sampleAcross = (z + .5) * request.CellSizeMeters;
			if (sampleAcross < 4 || sampleAcross > request.RingWidthMeters - 4) continue;
			// Avoid terrain sampling the empty corners of the square cell scan. This
			// is conservative by half a cell diagonal, so an edge tree can still be
			// considered while removing roughly a fifth of long-range samples.
			double aroundDelta = sampleAround - request.Focus.AroundMeters;
			if (aroundDelta > request.CircumferenceMeters * .5) aroundDelta -= request.CircumferenceMeters;
			else if (aroundDelta < -request.CircumferenceMeters * .5) aroundDelta += request.CircumferenceMeters;
			double acrossDelta = sampleAcross - request.Focus.AcrossMeters;
			double scanMargin = request.CellSizeMeters * .71;
			if (aroundDelta * aroundDelta + acrossDelta * acrossDelta > (request.MaximumDistanceMeters + scanMargin) * (request.MaximumDistanceMeters + scanMargin)) continue;
			long cellAround = (long)Math.Round(sampleAround); int cellAcross = (int)Math.Round(sampleAcross);
			TerrainSample sample = request.SurfaceEdits.GetPresentationTerrainSample(sampler, cellAround, cellAcross);
			float cellSurface = PresentationSurfaceHeight(request.SurfaceEdits, sampler, cellAround, cellAcross);
			if (IsSurfaceWater(request.SurfaceEdits, sampler, sample, cellSurface) || (sample.Biome != TerrainBiomeId.Forest && sample.Biome != TerrainBiomeId.Grassland)) continue;
			Vector3 cellWorld = request.Geometry.CanonicalToWorld(sampleAround, cellSurface, sampleAcross);
			Vector3 toCell = cellWorld - request.PlayerWorldPosition;
			float cellDistance = toCell.Length();
			bool behindFallback = cellDistance <= request.BehindCameraFallbackRadiusMeters;
			float frustumMargin = CalculateFrustumMargin(request.FrustumCellMarginMeters, cellDistance, request.FrustumPaddingRadians);
			if (!behindFallback && IsClearlyOutsideFrustum(cellWorld, request.CameraFrustum, frustumMargin)) continue;
			float forward = cellDistance > 0.01f ? request.CameraForward.Dot(toCell / cellDistance) : 1.0f;
			// Front cells are deliberately generated first.  The small nearby fallback
			// remains after a turn, so it cannot starve forever behind the player.
			float priority = (behindFallback ? 2_000_000.0f : 0.0f) + forward * 1_000_000.0f - cellDistance;
			cells.Add(new CellCandidate(x, z, sample, priority));
		}
		cells.Sort(static (a, b) => b.Priority.CompareTo(a.Priority));
		foreach (CellCandidate cellCandidate in cells)
		{
			long x = cellCandidate.AroundCell;
			long z = cellCandidate.AcrossCell;
			double sampleAround = (x + .5) * request.CellSizeMeters;
			double sampleAcross = (z + .5) * request.CellSizeMeters;
			for (int candidate = 0; candidate < request.Candidates && (lod1.Count < request.MaximumLod1 || lod2.Count < request.MaximumLod2 || lod3.Count < request.MaximumLod3 || lod4.Count < request.MaximumLod4 || lod5.Count < request.MaximumLod5); candidate++)
			{
				int salt = 11 + candidate * 17;
				double around = (x + .06 + Hash01(x, z, sampler.Seed, salt) * .88) * request.CellSizeMeters;
				double across = (z + .06 + Hash01(x, z, sampler.Seed, salt + 1) * .88) * request.CellSizeMeters;
				double aroundDelta = around - request.Focus.AroundMeters;
				if (aroundDelta > request.CircumferenceMeters * .5) aroundDelta -= request.CircumferenceMeters;
				else if (aroundDelta < -request.CircumferenceMeters * .5) aroundDelta += request.CircumferenceMeters;
				double distance = Math.Sqrt(aroundDelta * aroundDelta + (across - request.Focus.AcrossMeters) * (across - request.Focus.AcrossMeters));
				if (distance > request.MaximumDistanceMeters) continue;
				// Do not use the cell-centre sample for an offset tree. Exact column
				// sampling prevents trees hovering over edits, water, and biome edges.
				long candidateAround = (long)Math.Round(around);
				int candidateAcross = (int)Math.Round(across);
				TerrainSample sample = request.SurfaceEdits.GetPresentationTerrainSample(sampler, candidateAround, candidateAcross);
				float surfaceHeight = sampler.UsesRuntimeSurfaceGeneration
					? PresentationSurfaceHeight(request.SurfaceEdits, sampler, around, across)
					: PresentationSurfaceHeight(request.SurfaceEdits, sampler, candidateAround, candidateAcross);
				if (IsSurfaceWater(request.SurfaceEdits, sampler, sample, surfaceHeight) || (sample.Biome != TerrainBiomeId.Forest && sample.Biome != TerrainBiomeId.Grassland)) continue;
				float treeChance = sample.Biome == TerrainBiomeId.Forest ? request.ForestChance : request.GrasslandChance;
				if (Hash01(x, z, sampler.Seed, salt + 2) >= ApplyGroveDensity(treeChance, request.GroveDensity, x, z, sampler.Seed)) continue;
				if (!IsSlopeSuitable(request.Geometry, sampler, request.SurfaceEdits, candidateAround, candidateAcross, surfaceHeight, request.MaximumSlopeDegrees)) continue;
				float height = 5f + 9f * Hash01(x, z, sampler.Seed, salt + 3);
				float width = height * (.20f + .14f * Hash01(x, z, sampler.Seed, salt + 4));
				float yaw = Hash01(x, z, sampler.Seed, salt + 5) * Mathf.Tau;
				bool billboard = distance >= request.Lod3DistanceMeters;
				Transform3D transform = CreateTransform(request.Geometry, request.WorldToLayer, request.PlayerWorldPosition, around, across, RootAltitude(surfaceHeight, request.RootEmbedMeters), width, height, billboard, yaw);
				if (distance < request.Lod1DistanceMeters && lod1.Count < request.MaximumLod1) lod1.Add(new TreePlacement(transform, DeterministicVariant(candidateAround, candidateAcross, sampler.Seed, request.Lod1Variants)));
				else if (distance < request.Lod2DistanceMeters && lod2.Count < request.MaximumLod2) lod2.Add(new TreePlacement(transform, DeterministicVariant(candidateAround, candidateAcross, sampler.Seed, request.Lod2Variants)));
				else if (distance < request.Lod3DistanceMeters && lod3.Count < request.MaximumLod3) lod3.Add(new TreePlacement(transform, DeterministicVariant(candidateAround, candidateAcross, sampler.Seed, request.Lod3Variants)));
				else if (distance < request.MinimumLod5DistanceMeters && lod4.Count < request.MaximumLod4) lod4.Add(transform);
				else if (distance >= request.MinimumLod5DistanceMeters && lod5.Count < request.MaximumLod5) lod5.Add(transform);
			}
		}
		stopwatch.Stop();
		return new BuildResult(request.AroundCell, request.AcrossCell, request.SurfaceRevision,
			GroupTiles(lod1, request.SpatialTileMeters), GroupTiles(lod2, request.SpatialTileMeters),
			GroupTiles(lod3, request.SpatialTileMeters), GroupTiles(lod4, request.SpatialTileMeters),
			GroupTiles(lod5, request.SpatialTileMeters), stopwatch.Elapsed.TotalMilliseconds);
	}

	private static bool IsClearlyOutsideFrustum(Vector3 point, IReadOnlyList<Plane> frustum, float margin)
	{
		// Camera3D.GetFrustum() planes use negative signed distances for points on
		// the inside in Godot 4.6. Reject only the positive (outside) half-space.
		// This deliberately treats an empty frustum as no frustum restriction, so
		// camera-less/headless startup cannot empty all distant tree bands.
		foreach (Plane plane in frustum)
			if (plane.DistanceTo(point) > margin) return true;
		return false;
	}

	private static void ValidateFrustumConvention()
	{
		if (_frustumConventionValidated) return;
		_frustumConventionValidated = true;
		// This deterministic representative camera plane guards the sign used by
		// GetFrustum: a forward point is negative/inside, while the point behind
		// the plane is positive/outside. Exercise both the side and empty cases.
		Plane forwardPlane = new(Vector3.Back, 0.0f);
		bool valid = forwardPlane.DistanceTo(new Vector3(0, 0, -10)) < 0.0f
			&& forwardPlane.DistanceTo(new Vector3(0, 0, 10)) > 0.0f
			&& !IsClearlyOutsideFrustum(new Vector3(0, 0, -10), new[] { forwardPlane }, 0.5f)
			&& IsClearlyOutsideFrustum(new Vector3(0, 0, 10), new[] { forwardPlane }, 0.5f)
			&& !IsClearlyOutsideFrustum(Vector3.Zero, Array.Empty<Plane>(), 0.0f);
		if (!valid)
			GD.PushError("Tree frustum sign regression: inside must be negative and outside positive.");
	}

	// Pure so the conservative angular wedge can be checked independently of
	// Godot scene state. At 1 km with the default 11 degrees it admits ~194 m
	// beyond the raw frustum edge, plus the cell/tree footprint.
	private static float CalculateFrustumMargin(float physicalRadius, float distance, float paddingRadians)
		=> physicalRadius + Mathf.Max(0.0f, distance) * Mathf.Tan(Mathf.Max(0.0f, paddingRadians));

	// Pure helpers are deliberately independent of scene state: the smoke test
	// exercises their deterministic density and slope boundaries headlessly.
	internal static float ApplyGroveDensity(float biomeChance, float groveDensity, long x, long z, int seed)
	{
		float patch = Hash01(FloorDiv(x, 4), FloorDiv(z, 4), seed, 719);
		// The patch multiplier is centred on one, preserving average biome density
		// while moving probability into deterministic groves and clearings.
		float multiplier = Mathf.Lerp(1.0f, Mathf.Lerp(.35f, 1.65f, patch), Mathf.Clamp(groveDensity, 0.0f, 1.0f));
		return Mathf.Clamp(biomeChance * multiplier, 0.0f, 1.0f);
	}

	internal static bool IsSlopeAngleSuitable(float riseX, float riseZ, float sampleSpacing, float maximumDegrees)
	{
		float run = Mathf.Max(.001f, sampleSpacing * 2.0f);
		float degrees = Mathf.RadToDeg(Mathf.Atan(Mathf.Sqrt(riseX * riseX + riseZ * riseZ) / run));
		return degrees <= Mathf.Max(0.0f, maximumDegrees);
	}

	internal static float RootAltitude(float editedSurfaceHeight, float rootEmbedMeters)
		=> editedSurfaceHeight - Mathf.Clamp(rootEmbedMeters, .1f, .25f);

	private static bool IsSlopeSuitable(in RingLodGeometrySnapshot geometry, RingTerrainSampler sampler,
		RingLodSurfaceEditSnapshot edits, long around, int across, float height, float maximumDegrees)
	{
		const int probe = 2;
		float x0 = PresentationSurfaceHeight(edits, sampler, around - probe, across);
		float x1 = PresentationSurfaceHeight(edits, sampler, around + probe, across);
		float z0 = PresentationSurfaceHeight(edits, sampler, around, across - probe);
		float z1 = PresentationSurfaceHeight(edits, sampler, around, across + probe);
		_ = geometry; _ = height; // Heights are canonical vertical metres; ring curvature does not alter local grade.
		return IsSlopeAngleSuitable(x1 - x0, z1 - z0, probe, maximumDegrees);
	}

	internal static bool UsesFloatPresentation(RingLodSurfaceEditSnapshot edits, RingTerrainSampler sampler) => sampler.UsesRuntimeSurfaceGeneration || edits.UsesFloatSurface;
	internal static float PresentationSurfaceHeight(RingLodSurfaceEditSnapshot edits, RingTerrainSampler sampler, long around, int across) => UsesFloatPresentation(edits, sampler) ? edits.GetPresentationSurfaceHeight(sampler, around, across) : edits.GetSurfaceHeight(sampler, around, across);
	internal static float PresentationSurfaceHeight(RingLodSurfaceEditSnapshot edits, RingTerrainSampler sampler, double around, double across)
		=> sampler.UsesRuntimeSurfaceGeneration
			? edits.GetPresentationSurfaceHeight(sampler, around, across)
			: PresentationSurfaceHeight(edits, sampler, (long)Math.Round(around), (int)Math.Round(across));
	internal static bool IsSurfaceWater(RingLodSurfaceEditSnapshot edits, RingTerrainSampler sampler, TerrainSample sample, float surfaceHeight) => sample.HasSurfaceWater || (UsesFloatPresentation(edits, sampler) && surfaceHeight < 0.0f);

	private static long FloorDiv(long value, int divisor)
	{
		long quotient = value / divisor;
		return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
	}

	private static Transform3D CreateTransform(
		in RingLodGeometrySnapshot geometry,
		Transform3D worldToLayer,
		Vector3 playerWorldPosition,
		double around,
		double across,
		double altitude,
		float width,
		float height,
		bool billboard,
		float yaw)
	{
		Vector3 baseWorld = geometry.CanonicalToWorld(around, altitude, across);
		Vector3 up = (geometry.CanonicalToWorld(around, altitude + 3, across) - baseWorld).Normalized();
		Vector3 normal = (geometry.CanonicalToWorld(around + 3, altitude, across) - baseWorld).Normalized();
		Vector3 right = up.Cross(normal).Normalized();
		if (!billboard)
		{
			normal = (normal * Mathf.Cos(yaw) + right * Mathf.Sin(yaw)).Normalized();
			right = up.Cross(normal).Normalized();
			// Imported catalog meshes are normalized uniformly by LOD0 height; using
			// width on X/Z would apply crown width a second time and squash foliage.
			return worldToLayer * new Transform3D(new Basis(right * height, up * height, normal * height), baseWorld);
		}
		if (billboard)
		{
			Vector3 toPlayer = playerWorldPosition - baseWorld;
			normal = (toPlayer - up * toPlayer.Dot(up)).Normalized();
			if (normal.LengthSquared() < 0.001f)
				normal = (geometry.CanonicalToWorld(around + 3, altitude, across) - baseWorld).Normalized();
		}
		// QuadMesh lies in the local XY plane. The fixed local-up axis keeps
		// every billboard upright on the ring while Z faces the player.
		right = up.Cross(normal).Normalized();
		return worldToLayer * new Transform3D(new Basis(right * width, up * height, normal), baseWorld + up * height * .5f);
	}

	internal static Transform3D CreateTransformForRegression(in RingLodGeometrySnapshot geometry, float width, float height)
		=> CreateTransform(geometry, Transform3D.Identity, Vector3.Zero, 10.0, 10.0, 0.0, width, height, false, 0.0f);

	private static void Install(MultiMeshInstance3D instance, IReadOnlyList<Transform3D> transforms)
	{
		if (instance.Multimesh == null) return;
		MultiMesh multiMesh = instance.Multimesh;
		multiMesh.InstanceCount = transforms.Count;
		multiMesh.VisibleInstanceCount = transforms.Count;
		// MultiMesh.Buffer is a 3x4 row-major transform stream. Bulk assignment
		// avoids SetInstanceTransform's managed/native transition for every tree.
		var buffer = new float[transforms.Count * 12];
		float maximumWindEnvelope = 0;
		for (int i = 0; i < transforms.Count; i++)
		{
			Transform3D transform = transforms[i];
			Basis basis = transform.Basis;
			maximumWindEnvelope = Mathf.Max(maximumWindEnvelope, (instance.GlobalBasis * basis.Y).Length() * .6f);
			int offset = i * 12;
			buffer[offset] = basis.X.X; buffer[offset + 1] = basis.Y.X; buffer[offset + 2] = basis.Z.X; buffer[offset + 3] = transform.Origin.X;
			buffer[offset + 4] = basis.X.Y; buffer[offset + 5] = basis.Y.Y; buffer[offset + 6] = basis.Z.Y; buffer[offset + 7] = transform.Origin.Y;
			buffer[offset + 8] = basis.X.Z; buffer[offset + 9] = basis.Y.Z; buffer[offset + 10] = basis.Z.Z; buffer[offset + 11] = transform.Origin.Z;
		}
		multiMesh.Buffer = buffer;
		// CPU fallback includes the same hard shader envelope as GPU batches,
		// including imported species taller than the historical 14 m fixtures.
		instance.ExtraCullMargin = Mathf.Max(instance.ExtraCullMargin, maximumWindEnvelope + 8);
	}

	internal static long SpatialTileKey(Vector3 localOrigin, float tileMeters)
	{
		int span = Mathf.Max(1, Mathf.RoundToInt(tileMeters));
		long x = (long)Math.Floor(localOrigin.X / span);
		long z = (long)Math.Floor(localOrigin.Z / span);
		return (x << 32) ^ (uint)z;
	}

	private static Dictionary<SpatialBatchKey, List<Transform3D>> GroupTiles(IReadOnlyList<TreePlacement> transforms, float tileMeters)
	{
		var tiles = new Dictionary<SpatialBatchKey, List<Transform3D>>();
		foreach (TreePlacement placement in transforms)
		{
			long tileKey = SpatialTileKey(placement.Transform.Origin, tileMeters);
			var key = new SpatialBatchKey(tileKey, placement.Variant);
			if (!tiles.TryGetValue(key, out List<Transform3D>? batch)) tiles[key] = batch = new List<Transform3D>();
			batch.Add(placement.Transform);
		}
		return tiles;
	}

	private static Dictionary<long, List<Transform3D>> GroupTiles(IReadOnlyList<Transform3D> transforms, float tileMeters)
	{
		var tiles = new Dictionary<long, List<Transform3D>>();
		foreach (Transform3D transform in transforms)
		{
			long key = SpatialTileKey(transform.Origin, tileMeters);
			if (!tiles.TryGetValue(key, out List<Transform3D>? tile)) tiles[key] = tile = new List<Transform3D>();
			tile.Add(transform);
		}
		return tiles;
	}

	private static int CountTransforms(IReadOnlyDictionary<long, List<Transform3D>> tiles)
	{
		int count = 0; foreach (List<Transform3D> tile in tiles.Values) count += tile.Count; return count;
	}

	private static int CountTransforms(IReadOnlyDictionary<SpatialBatchKey, List<Transform3D>> tiles)
	{
		int count = 0; foreach (List<Transform3D> tile in tiles.Values) count += tile.Count; return count;
	}

	internal static int DeterministicVariant(long canonicalAround, long canonicalAcross, int seed, int variantCount)
	{
		int count = Mathf.Max(1, variantCount);
		return Mathf.Clamp((int)(Hash01(canonicalAround, canonicalAcross, seed, 991) * count), 0, count - 1);
	}

	private void InstallTiles(MultiMeshInstance3D template, Dictionary<long, MultiMeshInstance3D> pool, IReadOnlyDictionary<long, List<Transform3D>> tiles)
	{
		template.Visible = false;
		foreach ((long key, MultiMeshInstance3D instance) in pool) instance.Visible = tiles.ContainsKey(key);
		foreach ((long key, List<Transform3D> transforms) in tiles)
		{
			if (!pool.TryGetValue(key, out MultiMeshInstance3D? instance))
			{
				if (template.Multimesh?.Mesh == null || template.MaterialOverride == null)
					continue; // A partially torn-down layer cannot safely clone render state.
				int shadowLod = TreeSunShadowLodOf(template);
				instance = CreateInstances($"{template.Name}_Tile_{key}", template.Multimesh.Mesh, template.MaterialOverride,
					shadowLod >= 0, shadowLod);
				// MultiMesh already owns the tile's aggregate AABB. Only allow for tree
				// height and a little rebuild drift, not the full tile span.
				instance.ExtraCullMargin = CellSizeMeters + 32.0f;
				pool.Add(key, instance);
			}
			instance.Visible = true;
			Install(instance, transforms);
		}
	}

	private void InstallAuthoredTiles(MultiMeshInstance3D[] templates, Dictionary<SpatialBatchKey, MultiMeshInstance3D> pool, IReadOnlyDictionary<SpatialBatchKey, List<Transform3D>> tiles)
		=> InstallVariantTiles(templates, pool, tiles);

	private void InstallVariantTiles(MultiMeshInstance3D[] templates, Dictionary<SpatialBatchKey, MultiMeshInstance3D> pool, IReadOnlyDictionary<SpatialBatchKey, List<Transform3D>> tiles)
	{
		foreach (MultiMeshInstance3D template in templates) template.Visible = false;
		foreach ((SpatialBatchKey key, MultiMeshInstance3D instance) in pool) instance.Visible = tiles.ContainsKey(key);
		foreach ((SpatialBatchKey key, List<Transform3D> transforms) in tiles)
		{
			if (!pool.TryGetValue(key, out MultiMeshInstance3D? instance))
			{
				MultiMeshInstance3D template = templates[Mathf.Clamp(key.Variant, 0, templates.Length - 1)];
				if (template.Multimesh?.Mesh == null) continue;
				int shadowLod = TreeSunShadowLodOf(template);
				instance = CreateInstances($"{template.Name}_Tile_{key.TileKey}_{key.Variant}", template.Multimesh.Mesh, template.MaterialOverride,
					shadowLod >= 0, shadowLod);
				instance.ExtraCullMargin = CellSizeMeters + 32.0f;
				pool.Add(key, instance);
			}
			instance.Visible = true;
			Install(instance, transforms);
		}
	}

	private static float Hash01(long x, long z, int seed, int salt)
	{
		ulong value = unchecked((ulong)x) * 0x9E3779B185EBCA87UL ^ unchecked((ulong)z) * 0xC2B2AE3D27D4EB4FUL ^ unchecked((uint)(seed + salt * 7919));
		value ^= value >> 30; value *= 0xBF58476D1CE4E5B9UL; value ^= value >> 27; value *= 0x94D049BB133111EBUL;
		return (float)((value ^ (value >> 31)) >> 40) / 16777215.0f;
	}
}
