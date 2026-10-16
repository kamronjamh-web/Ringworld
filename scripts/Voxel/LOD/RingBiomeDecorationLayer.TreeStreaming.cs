using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Voxel.LOD;

// Canonical tiles deliberately live beside the older camera-centred builder while
// this demo migrates.  Tile records contain no view-dependent state: turning the
// camera can re-render cached records but can never resample terrain.
public partial class RingBiomeDecorationLayer
{
	internal readonly record struct ImportedForestPlacementRecord(ulong Id, long Around, int Across, float SurfaceHeight, float Width, float Height, float Yaw, int Variant, TreeSpecies Species);
	internal static List<ImportedForestPlacementRecord> BuildImportedForestReviewTile(RingTerrainSampler sampler, long tileAround, long tileAcross, long aroundCellCount, double aroundCellMeters, int seed, double circumference, double ringWidth, RingLodSurfaceEditSnapshot? editSnapshot = null)
	{
		var edits = editSnapshot ?? new RingLodSurfaceEditSnapshot(0, (long)Math.Ceiling(circumference), new Dictionary<RingTerrainColumnCoordinate, int>());
		var variants = ImportedTreeCatalog.Load();
		var widths = new float[variants.Count]; var heights = new float[variants.Count];
		foreach (ImportedTreeVariant variant in variants) { widths[variant.Index] = variant.WidthMeters; heights[variant.Index] = variant.HeightMeters; }
		var request = new TileBuildRequest(new VegetationTileKey(tileAround, tileAcross), aroundCellCount, aroundCellMeters, circumference,
			30.0f, 8, 12, ringWidth, seed, .72f, .20f, .70f, 32.0f, true, widths, heights, edits, 0,
			sampler.CreateIndependentCopy(), CancellationToken.None, string.Empty, string.Empty, 0, 0, false);
		TileBuildResult result = BuildVegetationTile(request);
		var records = new List<ImportedForestPlacementRecord>(result.Tile?.Trees.Count ?? 0);
		if (result.Tile != null) foreach (CachedTree tree in result.Tile.Trees)
			records.Add(new ImportedForestPlacementRecord(tree.Id, tree.Around, tree.Across, tree.SurfaceHeight, tree.Width, tree.Height, tree.Yaw, tree.Variant, tree.Species));
		return records;
	}
	internal static bool VerifyImportedForestCacheReview(RingTerrainSampler sampler, long tileAround, long tileAcross, long aroundCellCount, double aroundCellMeters, int seed, double circumference, double ringWidth, string path)
	{
		var edits = new RingLodSurfaceEditSnapshot(0, (long)Math.Ceiling(circumference), new Dictionary<RingTerrainColumnCoordinate, int>());
		var variants = ImportedTreeCatalog.Load(); var widths = new float[variants.Count]; var heights = new float[variants.Count];
		foreach (ImportedTreeVariant variant in variants) { widths[variant.Index] = variant.WidthMeters; heights[variant.Index] = variant.HeightMeters; }
		var request = new TileBuildRequest(new VegetationTileKey(tileAround, tileAcross), aroundCellCount, aroundCellMeters, circumference, 30, 8, 12, ringWidth, seed, .72f, .20f, .70f, 32, true, widths, heights, edits, 0, sampler.CreateIndependentCopy(), CancellationToken.None, string.Empty, string.Empty, 0, 0, false);
		const long reviewBudget = 1024L * 1024L * 1024L;
		TileBuildResult built = BuildVegetationTile(request); if (built.Tile == null || !TryWriteVegetationTile(path, Path.GetDirectoryName(path)!, reviewBudget, 0, built.Tile)) return false;
		if (!TryLoadVegetationTile(path, new VegetationTileKey(tileAround, tileAcross), 0, out VegetationTile? loaded) || loaded == null || loaded.Trees.Count != built.Tile.Trees.Count) return false;
		for (int i = 0; i < loaded.Trees.Count; i++) if (!loaded.Trees[i].Equals(built.Tile.Trees[i])) return false;
		using (var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Write, FileShare.None)) { stream.Position = sizeof(int); stream.WriteByte(0x7F); }
		if (TryLoadVegetationTile(path, new VegetationTileKey(tileAround, tileAcross), 0, out _)) return false;
		if (!TryWriteVegetationTile(path, Path.GetDirectoryName(path)!, reviewBudget, 0, built.Tile)) return false;
		// Header (28 bytes) + first tree's fields through rank (44 bytes): species is byte 72.
		using (var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Write, FileShare.None)) { stream.Position = 72; stream.WriteByte(0xFF); }
		return !TryLoadVegetationTile(path, new VegetationTileKey(tileAround, tileAcross), 0, out _);
	}
	// Version 4 includes regional identity and the edited-root support policy.
	// It does not share cache paths with former procedural-only records.
	private const int VegetationDiskCacheVersion = 4;
	private const int VegetationDiskCacheMagic = 0x56544732; // VTG2
	private const int MaximumTreesPerVegetationTile = 8_192;

	private readonly record struct VegetationTileKey(long Around, long Across);
	private readonly record struct CachedTree(ulong Id, long Around, int Across, float SurfaceHeight, float Width, float Height, float Yaw, int Candidate, float Rank, TreeSpecies Species, int Variant);
	private sealed class VegetationTile
	{
		public required VegetationTileKey Key;
		public required List<CachedTree> Trees;
		public ulong EditVersion;
		public long LastUsed;
		public long EstimatedBytes => 64L + Trees.Count * 64L;
	}
	private readonly record struct TileBuildRequest(VegetationTileKey Key, long AroundCellCount, double AroundCellMeters, double CircumferenceMeters,
		float AcrossCellMeters, int TileCells, int Candidates, double RingWidth, int Seed, float ForestChance, float GrasslandChance,
		float GroveDensity, float MaximumSlope, bool UseRegionalForest, float[] VariantWidths, float[] VariantHeights, RingLodSurfaceEditSnapshot Edits, ulong EditVersion, RingTerrainSampler Sampler,
		CancellationToken Cancellation, string DiskPath, string DiskBudgetRoot, long DiskBudgetBytes, long MinimumFreeBytes, bool AllowDiskCache);
	private readonly record struct TileBuildResult(VegetationTileKey Key, VegetationTile? Tile, double Milliseconds, bool Cancelled, bool DiskHit, bool DiskWritten);
	private readonly record struct ForestCandidateEvaluation(bool Viable, ulong Id, long CellX, long CellZ, int Candidate,
		long Around, int Across, TerrainSample Terrain, float Surface, ForestRegionSample Regional, float Chance);
	private readonly record struct VegetationLayout(
		long AroundCellCount,
		double AroundCellMeters,
		long AroundTileCount,
		double AroundTileMeters,
		float AcrossCellMeters,
		float AcrossTileMeters,
		int TileCells,
		double CircumferenceMeters);
	private sealed class VegetationBuildJob
	{
		public required CancellationTokenSource Cancellation;
		public required Task<TileBuildResult> Task;
	}

	private enum VegetationUploadPool : byte
	{
		Lod1Primary,
		Lod1Secondary,
		Lod2Primary,
		Lod2Secondary,
		Lod3Primary,
		Lod3Secondary,
		Lod4,
		Lod5
	}

	private readonly record struct VegetationUpload(
		VegetationUploadPool Pool,
		SpatialBatchKey VariantKey,
		long SimpleKey,
		List<Transform3D> Transforms);

	private sealed class VegetationRenderPlan
	{
		public required Dictionary<SpatialBatchKey, List<Transform3D>> Lod1;
		public required Dictionary<SpatialBatchKey, List<Transform3D>> Lod2;
		public required Dictionary<SpatialBatchKey, List<Transform3D>> Lod3;
		public required Dictionary<SpatialBatchKey, List<Transform3D>> Lod4;
		public required Dictionary<SpatialBatchKey, List<Transform3D>> Lod5;
		public required List<VegetationUpload> Uploads;
		public int NextUpload;
		public int UploadedTransformCount;
		public int LogicalTreeCount;
		public int RequiredRendererNodes;
	}

	[ExportGroup("Persistent vegetation streaming")]
	[Export(PropertyHint.Range, "256,4096,32")] public int MaximumCachedVegetationTiles { get; set; } = 768;
	[Export(PropertyHint.Range, "16,1024,16")] public int MaximumVegetationCacheMemoryMiB { get; set; } = 96;
	[Export(PropertyHint.Range, "128,4096,64")] public int MaximumVegetationRendererNodes { get; set; } = 1024;
	[Export(PropertyHint.Range, "1,16,1")] public int MaximumVegetationTileBuildsInFlight { get; set; } = 2;
	[Export(PropertyHint.Range, "0,10,0.25")] public float VegetationPredictionLookAheadSeconds { get; set; } = 2.0f;
	[Export(PropertyHint.Range, "0,10000,110")] public float VegetationPredictionMaximumMeters { get; set; } = 1320.0f;
	[Export(PropertyHint.Range, "1,64,1")] public int VegetationTilesPerRenderRefresh { get; set; } = 8;
	[Export(PropertyHint.Range, "1,64,1")] public int MaximumVegetationUploadsPerFrame { get; set; } = 12;
	[Export(PropertyHint.Range, "0.1,8,0.1")] public float VegetationUploadBudgetMilliseconds { get; set; } = 1.5f;
	[Export] public bool UsePersistentVegetationTiles { get; set; } = true;
	[Export] public bool UseRegionalImportedForest { get; set; } = true;
	[Export] public bool EnableVegetationDiskCache { get; set; } = true;
	// This is a shared per-world ceiling for every disposable file under lod_cache,
	// not an additional tree-only allowance.
	[Export(PropertyHint.Range, "0,8192,64")] public int VegetationDiskCacheBudgetMiB { get; set; } = 512;
	[Export(PropertyHint.Range, "512,16384,256")] public int VegetationDiskCacheMinimumFreeMiB { get; set; } = 2048;

	private readonly Dictionary<VegetationTileKey, VegetationTile> _vegetationTiles = new();
	private readonly PriorityQueue<VegetationTileKey, double> _vegetationRequests = new();
	private readonly HashSet<VegetationTileKey> _requestedVegetationTiles = new();
	private readonly Dictionary<VegetationTileKey, VegetationBuildJob> _vegetationBuilds = new();
	private HashSet<VegetationTileKey> _desiredVegetationTiles = new();
	private HashSet<VegetationTileKey> _visibleVegetationTiles = new();
	private HashSet<VegetationTileKey> _keepVegetationTiles = new();
	private int _vegetationKeepPerimeterTiles;
	private readonly HashSet<VegetationTileKey> _evictedVegetationKeys = new();
	private ulong _vegetationEditVersion = ulong.MaxValue;
	private long _vegetationUseClock;
	private long _vegetationTileGenerationEpoch;
	private long _lastVegetationAroundTile = long.MinValue;
	private long _lastVegetationAcrossTile = long.MinValue;
	private long _lastVegetationRenderCellAround = long.MinValue;
	private long _lastVegetationRenderCellAcross = long.MinValue;
	private Vector3 _lastVegetationPriorityForward = Vector3.Zero;
	private Vector3 _lastVegetationRenderRingCenter = new(float.PositiveInfinity, 0, 0);
	private bool _vegetationRenderDirty;
	private bool _vegetationRenderMovementDirty;
	private int _vegetationTilesChangedSinceRender;
	private VegetationRenderPlan? _pendingVegetationRenderPlan;
	private bool _rendererBudgetWarningIssued;
	private bool _vegetationCommandLineConfigured;
	private bool _vegetationDiskCacheDisabledByCommandLine;

	public long VegetationCacheHits { get; private set; }
	public long VegetationCacheMisses { get; private set; }
	public long VegetationTilesGenerated { get; private set; }
	public long VegetationTilesEvicted { get; private set; }
	/// <summary>Completed tile builds (generated or disk-loaded) for a key that had been evicted earlier.</summary>
	public long VegetationTilesReloadedAfterEviction { get; private set; }
	/// <summary>Full desired/keep-set replans. A stationary or translation-rebased layer must not add to this.</summary>
	public long VegetationPlanCount { get; private set; }
	public long VegetationTranslationRebaseCount { get; private set; }
	public long VegetationFullRebaseCount { get; private set; }
	public long VegetationTileBuildsStarted { get; private set; }
	public long VegetationTilesInvalidated { get; private set; }
	public long VegetationTileBuildsCancelled { get; private set; }
	public long VegetationTileResultsDiscarded { get; private set; }
	public int VegetationPendingTiles => _vegetationRequests.Count + _vegetationBuilds.Count;
	public int VegetationPendingRenderUploads => _pendingVegetationRenderPlan == null
		? 0
		: _pendingVegetationRenderPlan.Uploads.Count - _pendingVegetationRenderPlan.NextUpload;
	public int VegetationCachedTileCount => _vegetationTiles.Count;
	public long VegetationCacheEstimatedBytes { get; private set; }
	public int VegetationActiveRendererNodes { get; private set; }
	public int VegetationRendererNodeCount { get; private set; }
	public long VegetationRendererEstimatedBytes { get; private set; }
	public double LastVegetationTileBuildMilliseconds { get; private set; }
	public double LastVegetationRenderMilliseconds { get; private set; }
	public double MaxVegetationRenderMilliseconds { get; private set; }
	public double LastVegetationUploadMilliseconds { get; private set; }
	public long VegetationDiskCacheHits { get; private set; }
	public long VegetationDiskCacheWrites { get; private set; }
	public long VegetationDiskCacheBytes { get; private set; }

	private void QueueTileStreaming(bool force)
	{
		if (!UsePersistentVegetationTiles || _grid?.TerrainSampler is not RingTerrainSampler sampler || _explorer == null) return;
		EnsureVegetationCommandLineConfiguration();
		VegetationLayout layout = CaptureVegetationLayout();
		RingCanonicalPosition focus = _explorer.CanonicalPosition;
		Vector3 renderRingCenter = _grid.GetCanonicalRingCenterWorld();
		bool geometryRebased = renderRingCenter != _lastVegetationRenderRingCenter;
		_lastVegetationRenderRingCenter = renderRingCenter;
		if (geometryRebased)
			geometryRebased = ResolveVegetationRebase(_gpuTreePresentation && !_gpuTreeFailed && _gpuTreeGeometry is { } sourceGeometry
				&& TryGetGpuTreePureTranslationDelta(sourceGeometry, _grid.CaptureLodGeometrySnapshot(), focus, out _));
		Vector3 priorityForward = ResolveVegetationPriorityForward();
		RingLodSurfaceEditSnapshot edits = _smoothTerrain?.CaptureLodSurfaceEditSnapshot() ?? _grid.CaptureLodSurfaceEditSnapshot();
		if (edits.Version != _vegetationEditVersion)
		{
			// Preserve unaffected cache entries, but cancel every worker holding the
			// obsolete immutable edit snapshot. Completed tasks remain observed and
			// dispose their independent sampler normally.
			RequestCancelVegetationJobs();
			InvalidateVegetationTiles(edits, layout);
			_vegetationEditVersion = edits.Version;
			force = true;
		}

		RingCanonicalPosition predictedFocus = PredictVegetationFocus(focus);
		if (!PlanVegetationSets(focus, predictedFocus, layout, _grid.RingAcrossWidthMeters, geometryRebased, force, priorityForward, out bool priorityTurned))
		{
			if (priorityTurned)
			{
				RebuildVegetationRequestQueue(focus, predictedFocus, priorityForward, layout);
				StartVegetationTileBuilds(sampler, edits, layout);
			}
			return;
		}
		RebuildVegetationRequestQueue(focus, predictedFocus, priorityForward, layout);
		_vegetationRenderDirty = true;
		_vegetationRenderMovementDirty = true;
		StartVegetationTileBuilds(sampler, edits, layout);
		EvictVegetationCache();
		ScheduleVegetationDiskBudget();
	}

	// A floating-origin move that only translates the surface keeps every GPU source row valid
	// (TryRebaseGpuTreeSources follows the delta), so it needs no replan, refresh or render-plan reset.
	// Basis or shape changes, and the CPU renderer whose transforms are baked, take the full path (returns true).
	private bool ResolveVegetationRebase(bool translationOnly)
	{
		if (translationOnly)
		{
			VegetationTranslationRebaseCount++;
			return false;
		}
		VegetationFullRebaseCount++;
		_pendingVegetationRenderPlan = null;
		RefreshGpuTreeResidentSources(true);
		return true;
	}

	// Tile membership planning, free of scene and sampler access so the headless streaming contract can drive it.
	// Returns true when the desired/keep sets were recomputed; false when the focus stayed in its tile.
	private bool PlanVegetationSets(RingCanonicalPosition focus, RingCanonicalPosition predictedFocus, in VegetationLayout layout,
		double ringAcrossWidth, bool geometryRebased, bool force, Vector3 priorityForward, out bool priorityTurned)
	{
		long renderCellAround = Math.Clamp((long)Math.Floor(focus.AroundMeters / layout.AroundCellMeters), 0L, layout.AroundCellCount - 1L);
		long renderCellAcross = FloorDiv((long)Math.Floor(focus.AcrossMeters), Mathf.Max(1, Mathf.RoundToInt(layout.AcrossCellMeters)));
		long aroundTile = Math.Clamp(renderCellAround / layout.TileCells, 0L, layout.AroundTileCount - 1L);
		long acrossTile = FloorDiv((long)Math.Floor(focus.AcrossMeters), Mathf.Max(1, Mathf.RoundToInt(layout.AcrossTileMeters)));
		bool renderCellMoved = renderCellAround != _lastVegetationRenderCellAround
			|| renderCellAcross != _lastVegetationRenderCellAcross;
		priorityTurned = _lastVegetationPriorityForward.LengthSquared() > 0.01f
			&& _lastVegetationPriorityForward.Dot(priorityForward) < Mathf.Cos(Mathf.DegToRad(15.0f));
		if (!force && !geometryRebased && aroundTile == _lastVegetationAroundTile && acrossTile == _lastVegetationAcrossTile)
		{
			if (renderCellMoved)
			{
				_vegetationRenderDirty = true;
				_vegetationRenderMovementDirty = true;
			}
			_lastVegetationRenderCellAround = renderCellAround;
			_lastVegetationRenderCellAcross = renderCellAcross;
			if (priorityTurned) _lastVegetationPriorityForward = priorityForward;
			return false;
		}

		VegetationPlanCount++;
		_lastVegetationAroundTile = aroundTile;
		_lastVegetationAcrossTile = acrossTile;
		_lastVegetationRenderCellAround = renderCellAround;
		_lastVegetationRenderCellAcross = renderCellAcross;
		_lastVegetationPriorityForward = priorityForward;
		float collisionCoverage = UseRegionalImportedForest && EnableImportedForestCollisions
			? Mathf.Max(0.0f, ImportedForestCollisionRadiusMeters) + 4.0f : 0.0f;
		float maxDistance = Mathf.Max(Mathf.Max(VisibilityRadiusMeters, MaximumLod5DistanceMeters), collisionCoverage);
		HashSet<VegetationTileKey> visible = CollectVegetationTiles(focus, maxDistance, layout, ringAcrossWidth);
		var desired = new HashSet<VegetationTileKey>(visible);
		bool predicts = VegetationPredictionLookAheadSeconds > 0.0f && VegetationPredictionMaximumMeters > 0.0f;
		if (predicts) desired.UnionWith(CollectVegetationTiles(predictedFocus, maxDistance, layout, ringAcrossWidth));
		// Keep band: the build radius plus one more tile. Tiles in it are never evicted or cancelled, so a focus
		// jittering across a tile boundary re-adds nothing it just dropped.
		float keepRadius = maxDistance + (float)Math.Max(layout.AroundTileMeters, layout.AcrossTileMeters);
		HashSet<VegetationTileKey> keep = CollectVegetationTiles(focus, keepRadius, layout, ringAcrossWidth);
		if (predicts) keep.UnionWith(CollectVegetationTiles(predictedFocus, keepRadius, layout, ringAcrossWidth));
		keep.UnionWith(desired);
		_keepVegetationTiles = keep;
		_vegetationKeepPerimeterTiles = (int)Math.Ceiling(Math.PI * 2.0 * keepRadius / Math.Max(1.0, Math.Min(layout.AroundTileMeters, layout.AcrossTileMeters)));

		_visibleVegetationTiles = visible;
		RefreshGpuTreeResidentSources(true);
		_desiredVegetationTiles = desired;
		CancelObsoleteVegetationJobs(keep);
		foreach (VegetationTile tile in _vegetationTiles.Values)
		{
			if (!desired.Contains(tile.Key)) continue;
			tile.LastUsed = ++_vegetationUseClock;
			VegetationCacheHits++;
		}
		return true;
	}

	private VegetationLayout CaptureVegetationLayout()
		=> CaptureVegetationLayout(_grid!.GetCanonicalRingCircumferenceMeters64());

	private VegetationLayout CaptureVegetationLayout(double ringCircumferenceMeters)
	{
		double circumference = Math.Max(1.0, ringCircumferenceMeters);
		float acrossCellMeters = Mathf.Max(20.0f, CellSizeMeters);
		int tileCells = Mathf.Clamp(SpatialTileCells, 4, 24);
		long aroundCellCount = Math.Max(1L, (long)Math.Ceiling(circumference / acrossCellMeters));
		double aroundCellMeters = circumference / aroundCellCount;
		long aroundTileCount = Math.Max(1L, (long)Math.Ceiling(aroundCellCount / (double)tileCells));
		return new VegetationLayout(
			aroundCellCount,
			aroundCellMeters,
			aroundTileCount,
			aroundCellMeters * tileCells,
			acrossCellMeters,
			acrossCellMeters * tileCells,
			tileCells,
			circumference);
	}

	private RingCanonicalPosition PredictVegetationFocus(RingCanonicalPosition focus)
	{
		if (_explorer == null) return focus;
		Vector3 velocity = _explorer.CanonicalVelocity;
		double leadAround = velocity.X * Math.Max(0.0f, VegetationPredictionLookAheadSeconds);
		double leadAcross = velocity.Z * Math.Max(0.0f, VegetationPredictionLookAheadSeconds);
		double leadLength = Math.Sqrt(leadAround * leadAround + leadAcross * leadAcross);
		double maximumLead = Math.Max(0.0f, VegetationPredictionMaximumMeters);
		if (leadLength > maximumLead && leadLength > 0.001)
		{
			double scale = maximumLead / leadLength;
			leadAround *= scale;
			leadAcross *= scale;
		}
		double circumference = Math.Max(1.0, _grid!.GetCanonicalRingCircumferenceMeters64());
		double around = (focus.AroundMeters + leadAround) % circumference;
		if (around < 0.0) around += circumference;
		double across = Math.Clamp(focus.AcrossMeters + leadAcross, 0.0, _grid.RingAcrossWidthMeters);
		return new RingCanonicalPosition(around, focus.VerticalMeters, across);
	}

	private HashSet<VegetationTileKey> CollectVegetationTiles(RingCanonicalPosition center, float radiusMeters, in VegetationLayout layout, double ringAcrossWidth)
	{
		long centerAroundCell = Math.Clamp((long)Math.Floor(center.AroundMeters / layout.AroundCellMeters), 0L, layout.AroundCellCount - 1L);
		long centerAroundTile = centerAroundCell / layout.TileCells;
		long centerAcrossTile = FloorDiv((long)Math.Floor(center.AcrossMeters), Mathf.Max(1, Mathf.RoundToInt(layout.AcrossTileMeters)));
		int aroundRadius = Math.Max(1, (int)Math.Ceiling(radiusMeters / layout.AroundTileMeters) + 1);
		int acrossRadius = Math.Max(1, Mathf.CeilToInt(radiusMeters / layout.AcrossTileMeters) + 1);
		double margin = Math.Max(layout.AroundTileMeters, layout.AcrossTileMeters);
		double permittedSquared = (radiusMeters + margin) * (radiusMeters + margin);
		var result = new HashSet<VegetationTileKey>();
		for (long z = centerAcrossTile - acrossRadius; z <= centerAcrossTile + acrossRadius; z++)
		{
			if (z < 0 || z * layout.AcrossTileMeters >= ringAcrossWidth) continue;
			for (long rawX = centerAroundTile - aroundRadius; rawX <= centerAroundTile + aroundRadius; rawX++)
			{
				long x = PositiveModulo(rawX, layout.AroundTileCount);
				(double tileAround, double tileAcross) = VegetationTileCenter(new VegetationTileKey(x, z), layout);
				double da = WrappedVegetationDelta(tileAround - center.AroundMeters, layout.CircumferenceMeters);
				double dz = tileAcross - center.AcrossMeters;
				if (da * da + dz * dz <= permittedSquared) result.Add(new VegetationTileKey(x, z));
			}
		}
		return result;
	}

	private void RebuildVegetationRequestQueue(
		RingCanonicalPosition focus,
		RingCanonicalPosition predictedFocus,
		Vector3 cameraForward,
		in VegetationLayout layout)
	{
		_vegetationRequests.Clear();
		_requestedVegetationTiles.Clear();
		foreach (VegetationTileKey key in _vegetationBuilds.Keys) _requestedVegetationTiles.Add(key);
		RingLodGeometrySnapshot geometry = _grid!.CaptureLodGeometrySnapshot();
		Vector3 playerWorld = _explorer!.GlobalPosition;
		foreach (VegetationTileKey key in _desiredVegetationTiles)
		{
			if (_vegetationTiles.ContainsKey(key) || _vegetationBuilds.ContainsKey(key)) continue;
			double priority = VegetationRequestPriority(key, focus, predictedFocus, cameraForward, geometry, playerWorld, layout);
			_vegetationRequests.Enqueue(key, priority);
			_requestedVegetationTiles.Add(key);
			VegetationCacheMisses++;
		}
	}

	private double VegetationRequestPriority(
		VegetationTileKey key,
		RingCanonicalPosition focus,
		RingCanonicalPosition predictedFocus,
		Vector3 cameraForward,
		in RingLodGeometrySnapshot geometry,
		Vector3 playerWorld,
		in VegetationLayout layout)
	{
		(double around, double across) = VegetationTileCenter(key, layout);
		double currentAround = WrappedVegetationDelta(around - focus.AroundMeters, layout.CircumferenceMeters);
		double predictedAround = WrappedVegetationDelta(around - predictedFocus.AroundMeters, layout.CircumferenceMeters);
		double currentAcross = across - focus.AcrossMeters;
		double predictedAcross = across - predictedFocus.AcrossMeters;
		double currentDistanceSquared = currentAround * currentAround + currentAcross * currentAcross;
		double predictedDistanceSquared = predictedAround * predictedAround + predictedAcross * predictedAcross;
		double near = Math.Max(Lod3DistanceMeters, layout.AcrossTileMeters * 2.0);
		int band;
		if (currentDistanceSquared <= near * near)
		{
			band = 0;
		}
		else if (_visibleVegetationTiles.Contains(key))
		{
			Vector3 tileWorld = geometry.CanonicalToWorld(around, focus.VerticalMeters, across);
			Vector3 direction = tileWorld - playerWorld;
			float facing = direction.LengthSquared() > 0.01f ? cameraForward.Dot(direction.Normalized()) : 1.0f;
			band = facing >= -0.15f ? 1 : 2;
		}
		else
		{
			band = 3;
		}
		return band * 1.0e18 + Math.Min(currentDistanceSquared, predictedDistanceSquared);
	}

	private static (double Around, double Across) VegetationTileCenter(VegetationTileKey key, in VegetationLayout layout)
	{
		long firstCell = key.Around * layout.TileCells;
		long cellCount = Math.Max(1L, Math.Min(layout.TileCells, layout.AroundCellCount - firstCell));
		double around = (firstCell + cellCount * 0.5) * layout.AroundCellMeters;
		double across = (key.Across + 0.5) * layout.AcrossTileMeters;
		return (around, across);
	}

	private static double WrappedVegetationDelta(double delta, double circumference)
	{
		if (delta > circumference * 0.5) delta -= circumference;
		else if (delta < -circumference * 0.5) delta += circumference;
		return delta;
	}

	private Vector3 ResolveVegetationPriorityForward()
	{
		Camera3D? camera = GetViewport().GetCamera3D();
		Vector3 forward = camera != null ? -camera.GlobalTransform.Basis.Z : -_explorer!.GlobalTransform.Basis.Z;
		return forward.LengthSquared() > 0.001f ? forward.Normalized() : Vector3.Forward;
	}

	private void StartVegetationTileBuilds(RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits, in VegetationLayout layout)
	{
		int limit = Mathf.Clamp(MaximumVegetationTileBuildsInFlight, 1, 16);
		while (_vegetationBuilds.Count < limit && _vegetationRequests.Count > 0)
		{
			VegetationTileKey key = _vegetationRequests.Dequeue();
			if (!_desiredVegetationTiles.Contains(key) || _vegetationTiles.ContainsKey(key) || _vegetationBuilds.ContainsKey(key))
			{
				_requestedVegetationTiles.Remove(key);
				continue;
			}
			(string diskPath, string budgetRoot) = ResolveVegetationDiskCachePath(key, sampler, layout);
			var cancellation = new CancellationTokenSource();
			bool allowDisk = EnableVegetationDiskCache
				&& !_vegetationDiskCacheDisabledByCommandLine
				&& VegetationDiskCacheBudgetMiB > 0
				&& !TileTouchesEditedColumns(key, edits, layout);
			var request = new TileBuildRequest(
				key,
				layout.AroundCellCount,
				layout.AroundCellMeters,
				layout.CircumferenceMeters,
				layout.AcrossCellMeters,
				layout.TileCells,
				Mathf.Clamp(CandidatesPerCell, 1, 12),
				_grid!.RingAcrossWidthMeters,
				sampler.Seed,
				ForestSpawnChance,
				GrasslandSpawnChance,
				GroveDensity,
				MaximumSlopeDegrees,
				UseRegionalImportedForest,
				UseRegionalImportedForest ? CaptureImportedVariantWidths() : Array.Empty<float>(),
				UseRegionalImportedForest ? CaptureImportedVariantHeights() : Array.Empty<float>(),
				edits,
				edits.Version,
				sampler.CreateIndependentCopy(),
				cancellation.Token,
				diskPath,
				budgetRoot,
				VegetationDiskCacheBudgetMiB * 1024L * 1024L,
				VegetationDiskCacheMinimumFreeMiB * 1024L * 1024L,
				allowDisk);
			VegetationTileBuildsStarted++;
			_vegetationBuilds[key] = new VegetationBuildJob
			{
				Cancellation = cancellation,
				Task = Task.Run(() => BuildVegetationTile(request))
			};
		}
	}

	private static float[] CaptureImportedVariantWidths()
	{
		var values = new float[ImportedTreeCatalog.VariantCount];
		foreach (ImportedTreeVariant variant in ImportedTreeCatalog.Load()) values[variant.Index] = variant.WidthMeters;
		return values;
	}

	private static float[] CaptureImportedVariantHeights()
	{
		var values = new float[ImportedTreeCatalog.VariantCount];
		foreach (ImportedTreeVariant variant in ImportedTreeCatalog.Load()) values[variant.Index] = variant.HeightMeters;
		return values;
	}

	private void CompleteTileStreaming()
	{
		using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Trees install");
		if (!UsePersistentVegetationTiles || _grid?.TerrainSampler is not RingTerrainSampler sampler) return;
		bool completedAny = false;
		bool rebuildRequests = false;
		foreach ((VegetationTileKey key, VegetationBuildJob job) in new List<KeyValuePair<VegetationTileKey, VegetationBuildJob>>(_vegetationBuilds))
		{
			if (!job.Task.IsCompleted) continue;
			completedAny = true;
			_vegetationBuilds.Remove(key);
			_requestedVegetationTiles.Remove(key);
			job.Cancellation.Dispose();
			if (job.Task.IsFaulted || job.Task.IsCanceled)
			{
				VegetationTileResultsDiscarded++;
				rebuildRequests |= _desiredVegetationTiles.Contains(key);
				continue;
			}
			TileBuildResult result = job.Task.Result;
			LastVegetationTileBuildMilliseconds = result.Milliseconds;
			if (result.DiskHit) VegetationDiskCacheHits++;
			if (result.DiskWritten) VegetationDiskCacheWrites++;
			if (result.Cancelled)
			{
				VegetationTileBuildsCancelled++;
				rebuildRequests |= _desiredVegetationTiles.Contains(key);
				continue;
			}
			if (result.Tile == null || result.Tile.EditVersion != _vegetationEditVersion)
			{
				VegetationTileResultsDiscarded++;
				rebuildRequests |= _desiredVegetationTiles.Contains(key);
				continue;
			}
			// Keep a completed current-version tile even if the player just left its
			// render window. It was already paid for and remains useful to the LRU.
			result.Tile.LastUsed = ++_vegetationUseClock;
			_vegetationTiles[key] = result.Tile;
			if (_evictedVegetationKeys.Remove(key)) VegetationTilesReloadedAfterEviction++;
			_vegetationTileGenerationEpoch++;
			if (!result.DiskHit) VegetationTilesGenerated++;
			if (_visibleVegetationTiles.Contains(key))
			{
				_vegetationTilesChangedSinceRender++;
				_vegetationRenderDirty = true;
				// Coalesce completed tiles before rebuilding GPU source buffers. Camera
				// culling continues against the current resident source set meanwhile.
				RefreshGpuTreeResidentSources();
			}
		}
		if (!completedAny)
		{
			// The settled-state hot path only advances staged GPU uploads. Cache
			// scans, LRU walks and edit-snapshot captures are completion-driven.
			ServiceVegetationRenderUpdates();
			return;
		}

		VegetationLayout layout = CaptureVegetationLayout();
		if (completedAny && rebuildRequests)
		{
			RingCanonicalPosition focus = _explorer!.CanonicalPosition;
			RebuildVegetationRequestQueue(focus, PredictVegetationFocus(focus), ResolveVegetationPriorityForward(), layout);
		}
		RingLodSurfaceEditSnapshot edits = _smoothTerrain?.CaptureLodSurfaceEditSnapshot() ?? _grid.CaptureLodSurfaceEditSnapshot();
		StartVegetationTileBuilds(sampler, edits, layout);
		EvictVegetationCache();
		ScheduleVegetationDiskBudget();
		ServiceVegetationRenderUpdates();
	}

	private (string Path, string BudgetRoot) ResolveVegetationDiskCachePath(
		VegetationTileKey key,
		RingTerrainSampler sampler,
		in VegetationLayout layout)
	{
		if (!EnableVegetationDiskCache || _vegetationDiskCacheDisabledByCommandLine || VegetationDiskCacheBudgetMiB <= 0)
			return (string.Empty, string.Empty);
		string activeWorld = ProjectSettings.GetSetting("ringworld/runtime/active_world_directory", "").AsString();
		if (string.IsNullOrWhiteSpace(activeWorld)) return (string.Empty, string.Empty);
		string worldRoot = ProjectSettings.GlobalizePath(activeWorld);
		string budgetRoot = Path.Combine(worldRoot, "lod_cache");
		ulong settings = VegetationCacheSettingsHash(
			layout.AroundCellCount,
			layout.CircumferenceMeters,
			layout.AcrossCellMeters,
			(float)_grid!.RingAcrossWidthMeters,
			layout.TileCells,
			Mathf.Clamp(CandidatesPerCell, 1, 12),
			ForestSpawnChance,
			GrasslandSpawnChance,
			GroveDensity,
			MaximumSlopeDegrees,
			UseRegionalImportedForest);
		string definition = FormattableString.Invariant(
			$"v{VegetationDiskCacheVersion}|g{sampler.TerrainGeneratorVersion}|seed{sampler.Seed}|h{sampler.TerrainDefinitionHash:X16}|s{settings:X16}{(sampler.EmitterSites != null ? "|es1" : "")}");
		byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(definition));
		string identity = Convert.ToHexString(digest.AsSpan(0, 12));
		long shardAround = FloorDiv(key.Around, 64);
		long shardAcross = FloorDiv(key.Across, 64);
		string directory = Path.Combine(budgetRoot, "vegetation", identity, $"r_{shardAround}_{shardAcross}");
		return (Path.Combine(directory, $"t_{key.Around}_{key.Across}.bin"), budgetRoot);
	}

	private bool TileTouchesEditedColumns(VegetationTileKey key, RingLodSurfaceEditSnapshot edits, in VegetationLayout layout)
	{
		foreach (RingTerrainColumnCoordinate column in edits.CanonicalEditedColumns.Keys)
		{
			long editedAround = AroundTileForCanonical(column.AroundVoxel, layout);
			long aroundDelta = Math.Abs(key.Around - editedAround);
			aroundDelta = Math.Min(aroundDelta, layout.AroundTileCount - aroundDelta);
			long editedAcross = FloorDiv(column.AcrossVoxel, Mathf.Max(1, Mathf.RoundToInt(layout.AcrossTileMeters)));
			if (aroundDelta <= 1 && Math.Abs(key.Across - editedAcross) <= 1) return true;
		}
		return false;
	}

	private void ScheduleVegetationDiskBudget()
	{
		if (!EnableVegetationDiskCache || _vegetationDiskCacheDisabledByCommandLine) return;
		string activeWorld = ProjectSettings.GetSetting("ringworld/runtime/active_world_directory", "").AsString();
		if (string.IsNullOrWhiteSpace(activeWorld)) return;
		string root = Path.Combine(ProjectSettings.GlobalizePath(activeWorld), "lod_cache");
		RingGeneratedLodCacheBudget.Schedule(
			root,
			Mathf.Max(0, VegetationDiskCacheBudgetMiB) * 1024L * 1024L,
			75,
			TimeSpan.FromSeconds(30));
		if (RingGeneratedLodCacheBudget.TryGetLatest(root, out RingGeneratedLodCacheBudget.MaintenanceResult result))
			VegetationDiskCacheBytes = result.BytesAfter;
	}

	private void InvalidateVegetationTiles(RingLodSurfaceEditSnapshot edits, in VegetationLayout layout)
	{
		if (_vegetationEditVersion == ulong.MaxValue) return;
		var affected = new HashSet<VegetationTileKey>();
		foreach (RingTerrainColumnCoordinate dirty in edits.CanonicalDirtyColumns)
		{
			long around = AroundTileForCanonical(dirty.AroundVoxel, layout);
			long across = FloorDiv(dirty.AcrossVoxel, Mathf.Max(1, Mathf.RoundToInt(layout.AcrossTileMeters)));
			for (long z = across - 1; z <= across + 1; z++) for (long x = around - 1; x <= around + 1; x++)
				affected.Add(new VegetationTileKey(PositiveModulo(x, layout.AroundTileCount), z));
		}
		foreach (VegetationTileKey key in affected)
			if (_vegetationTiles.Remove(key)) VegetationTilesInvalidated++;
		if (affected.Count > 0) _vegetationRenderDirty = true;
	}

	private void RequestCancelVegetationJobs()
	{
		foreach (VegetationBuildJob job in _vegetationBuilds.Values) job.Cancellation.Cancel();
		_vegetationRequests.Clear();
		_requestedVegetationTiles.Clear();
	}

	// Only builds that have left the keep band are worthless; one inside it still lands in the cache.
	private void CancelObsoleteVegetationJobs(IReadOnlySet<VegetationTileKey> keep)
	{
		foreach ((VegetationTileKey key, VegetationBuildJob job) in _vegetationBuilds)
			if (!keep.Contains(key)) job.Cancellation.Cancel();
	}

	private void ShutdownVegetationJobs()
	{
		RequestCancelVegetationJobs();
		Task[] tasks = new Task[_vegetationBuilds.Count];
		int index = 0;
		foreach (VegetationBuildJob job in _vegetationBuilds.Values) tasks[index++] = job.Task;
		try { if (tasks.Length > 0) Task.WaitAll(tasks, 1500); }
		catch (AggregateException) { }
		foreach (VegetationBuildJob job in _vegetationBuilds.Values) job.Cancellation.Dispose();
		_vegetationBuilds.Clear();
	}

	public override void _ExitTree()
	{
        DisposeStartupResources();
		ClearGpuTreeResidentSources("Disposed");
		ShutdownVegetationJobs();
	}

	private void SuspendVegetationStreaming()
	{
		RequestCancelVegetationJobs();
		_desiredVegetationTiles.Clear();
		_keepVegetationTiles.Clear();
		_visibleVegetationTiles.Clear();
		_pendingVegetationRenderPlan = null;
		ClearGpuTreeResidentSources("Suspended");
		SetVegetationPoolTargets(_lod1TrunkTiles, Array.Empty<SpatialBatchKey>());
		SetVegetationPoolTargets(_lod1CanopyTiles, Array.Empty<SpatialBatchKey>());
		SetVegetationPoolTargets(_lod2TrunkTiles, Array.Empty<SpatialBatchKey>());
		SetVegetationPoolTargets(_lod2CanopyTiles, Array.Empty<SpatialBatchKey>());
		SetVegetationPoolTargets(_lod3TrunkTiles, Array.Empty<SpatialBatchKey>());
		SetVegetationPoolTargets(_lod3CanopyTiles, Array.Empty<SpatialBatchKey>());
		SetVegetationPoolTargets(_vegetationLod4Tiles, Array.Empty<SpatialBatchKey>());
		SetVegetationPoolTargets(_vegetationLod5Tiles, Array.Empty<SpatialBatchKey>());
		PruneRendererPools();
		_lastVegetationAroundTile = long.MinValue;
		_lastVegetationAcrossTile = long.MinValue;
		_vegetationRenderDirty = true;
		_vegetationRenderMovementDirty = true;
	}

	private void EvictVegetationCache()
	{
		// The cache must hold the whole desired set plus a margin of two perimeters, whatever the setting says,
		// or tiles are evicted the moment they leave the set and reloaded when the player turns back.
		int maximum = Math.Max(Mathf.Max(256, MaximumCachedVegetationTiles), _desiredVegetationTiles.Count + 2 * _vegetationKeepPerimeterTiles);
		long maximumBytes = Mathf.Max(16, MaximumVegetationCacheMemoryMiB) * 1024L * 1024L;
		long bytes = 0;
		foreach (VegetationTile tile in _vegetationTiles.Values) bytes += tile.EstimatedBytes;
		while (_vegetationTiles.Count > maximum || bytes > maximumBytes)
		{
			VegetationTileKey victim = default; long oldest = long.MaxValue; bool found = false;
			foreach ((VegetationTileKey key, VegetationTile tile) in _vegetationTiles)
				if (!_keepVegetationTiles.Contains(key) && !_desiredVegetationTiles.Contains(key) && tile.LastUsed < oldest) { victim = key; oldest = tile.LastUsed; found = true; }
			if (!found) break; // Never create a visible coverage hole merely to satisfy a too-small cache setting.
			bytes -= _vegetationTiles[victim].EstimatedBytes;
			_vegetationTiles.Remove(victim);
			_evictedVegetationKeys.Add(victim);
			VegetationTilesEvicted++;
		}
		VegetationCacheEstimatedBytes = Math.Max(0L, bytes);
	}

	private void ServiceVegetationRenderUpdates()
	{
		if (ServiceGpuTreeRenderUpdates()) return;
		if (_pendingVegetationRenderPlan == null && _vegetationRenderDirty)
		{
			bool urgent = LastInstalledTransformCount == 0 && _vegetationTilesChangedSinceRender > 0;
			bool accumulated = _vegetationTilesChangedSinceRender >= Mathf.Max(1, VegetationTilesPerRenderRefresh);
			bool settled = VegetationPendingTiles == 0;
			if (urgent || accumulated || settled || _vegetationRenderMovementDirty)
				_pendingVegetationRenderPlan = PrepareVegetationRenderPlan();
		}
		if (_pendingVegetationRenderPlan == null) return;

		VegetationRenderPlan plan = _pendingVegetationRenderPlan;
		Stopwatch uploadTimer = Stopwatch.StartNew();
		int maximumUploads = Mathf.Max(1, MaximumVegetationUploadsPerFrame);
		double budget = Math.Max(0.1f, VegetationUploadBudgetMilliseconds);
		int uploaded = 0;
		while (plan.NextUpload < plan.Uploads.Count && uploaded < maximumUploads)
		{
			VegetationUpload upload = plan.Uploads[plan.NextUpload++];
			ApplyVegetationUpload(upload);
			plan.UploadedTransformCount += upload.Transforms.Count;
			uploaded++;
			if (uploadTimer.Elapsed.TotalMilliseconds >= budget) break;
		}
		uploadTimer.Stop();
		LastVegetationUploadMilliseconds = uploadTimer.Elapsed.TotalMilliseconds;
		if (plan.NextUpload < plan.Uploads.Count) return;

		FinalizeVegetationRenderPlan(plan);
		_pendingVegetationRenderPlan = null;
	}

	private VegetationRenderPlan? PrepareVegetationRenderPlan()
	{
		if (_grid?.TerrainSampler is not RingTerrainSampler sampler || _explorer == null || _lod4Trees == null || _lod5Trees == null)
			return null;
		Stopwatch timer = Stopwatch.StartNew();
		RingCanonicalPosition focus = _explorer.CanonicalPosition;
		RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
		Transform3D inverse = GlobalTransform.AffineInverse();
		Vector3 player = _explorer.GlobalPosition;
		float collisionCoverage = UseRegionalImportedForest && EnableImportedForestCollisions
			? Mathf.Max(1.0f, ImportedForestCollisionRadiusMeters) : 1.0f;
		float lod1Distance = Mathf.Max(collisionCoverage + 4.0f, Lod1DistanceMeters);
		float lod2Distance = Mathf.Max(lod1Distance, Lod2DistanceMeters);
		float lod3Distance = Mathf.Max(lod2Distance, Lod3DistanceMeters);
		float lod5Min = Mathf.Clamp(MinimumLod5DistanceMeters, lod3Distance, Mathf.Max(lod3Distance, MaximumLod5DistanceMeters));
		float maxDistance = Mathf.Max(Mathf.Max(VisibilityRadiusMeters, MaximumLod5DistanceMeters), collisionCoverage + 4.0f);
		double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
		var candidates = new List<(CachedTree Tree, double Distance)>();
		foreach (VegetationTile tile in _vegetationTiles.Values)
		{
			if (!_visibleVegetationTiles.Contains(tile.Key)) continue;
			foreach (CachedTree tree in tile.Trees)
			{
				double da = WrappedVegetationDelta(tree.Around - focus.AroundMeters, circumference);
				double dz = tree.Across - focus.AcrossMeters;
				double distance = Math.Sqrt(da * da + dz * dz);
				if (distance > maxDistance) continue;
				int band = distance < lod1Distance ? 1
					: distance < lod2Distance ? 2
					: distance < lod3Distance ? 3
					: distance < lod5Min ? 4 : 5;
				if (tree.Rank <= VegetationLodRankGate(band)) candidates.Add((tree, distance));
			}
		}
		// Tree rank is view-independent. Distant populations are strict subsets of
		// nearer populations, while this final ordering enforces hard safety caps.
		float collisionPriorityRadius = UseRegionalImportedForest && EnableImportedForestCollisions
			? Mathf.Max(0.0f, ImportedForestCollisionRadiusMeters) : 0.0f;
		candidates.Sort((left, right) =>
		{
			// Gameplay colliders are independent of graphics quality. Reserve the
			// nearby physical neighborhood before rank-based far-population caps so
			// an obstacle never becomes invisible merely because distant groups won.
			bool leftNear = left.Distance <= collisionPriorityRadius;
			bool rightNear = right.Distance <= collisionPriorityRadius;
			if (leftNear != rightNear) return leftNear ? -1 : 1;
			if (leftNear)
			{
				int distance = left.Distance.CompareTo(right.Distance);
				if (distance != 0) return distance;
			}
			int rank = left.Tree.Rank.CompareTo(right.Tree.Rank);
			return rank != 0 ? rank : left.Tree.Id.CompareTo(right.Tree.Id);
		});
		int maximumLod1 = Math.Max(Mathf.Clamp(MaximumLod1Trees, 32, 2_048),
			UseRegionalImportedForest && EnableImportedForestCollisions ? MaximumImportedForestColliders : 0);
		int maximumLod2 = Mathf.Clamp(MaximumLod2Trees, 64, 4_096);
		int maximumLod3 = Mathf.Clamp(MaximumLod3Trees, 128, 8_192);
		int maximumLod4 = Mathf.Clamp(MaximumLod4Trees, 256, 16_384);
		int maximumLod5 = Mathf.Clamp(MaximumTreeBillboards, 256, 65_536);
		var lod1 = new List<TreePlacement>(maximumLod1);
		var lod2 = new List<TreePlacement>(maximumLod2);
		var lod3 = new List<TreePlacement>(maximumLod3);
		var lod4 = new List<TreePlacement>(maximumLod4);
		var lod5 = new List<TreePlacement>(maximumLod5);
		foreach ((CachedTree tree, double distance) in candidates)
		{
			if ((distance < lod1Distance && lod1.Count >= maximumLod1)
				|| (distance >= lod1Distance && distance < lod2Distance && lod2.Count >= maximumLod2)
				|| (distance >= lod2Distance && distance < lod3Distance && lod3.Count >= maximumLod3)
				|| (distance >= lod3Distance && distance < lod5Min && lod4.Count >= maximumLod4)
				|| (distance >= lod5Min && lod5.Count >= maximumLod5))
			{
				continue;
			}
			// Imported LOD3 is authored crossed cards. It keeps the canonical yaw,
			// rather than camera-facing billboarding, so species never changes by LOD.
			bool billboard = !UseRegionalImportedForest && distance >= lod3Distance;
			Transform3D transform = CreateTransform(
				geometry,
				inverse,
				player,
				tree.Around,
				tree.Across,
				RootAltitude(tree.SurfaceHeight, RootEmbedMeters),
				tree.Width,
				tree.Height,
				billboard,
				tree.Yaw);
			if (distance < lod1Distance)
				lod1.Add(new TreePlacement(transform, UseRegionalImportedForest ? tree.Variant : DeterministicVariant(tree.Around, tree.Across, sampler.Seed, _lod1AuthoredTemplates?.Length ?? 1)));
			else if (distance < lod2Distance)
				lod2.Add(new TreePlacement(transform, UseRegionalImportedForest ? tree.Variant : DeterministicVariant(tree.Around, tree.Across, sampler.Seed, _lod2AuthoredTemplates?.Length ?? 1)));
			else if (distance < lod3Distance)
				lod3.Add(new TreePlacement(transform, UseRegionalImportedForest ? tree.Variant : DeterministicVariant(tree.Around, tree.Across, sampler.Seed, _lod3AuthoredTemplates?.Length ?? 1)));
			else if (distance < lod5Min)
				lod4.Add(new TreePlacement(transform, UseRegionalImportedForest ? tree.Variant : 0));
			else
				lod5.Add(new TreePlacement(transform, UseRegionalImportedForest ? tree.Variant : 0));
		}

		float closeTileMeters = Mathf.Max(20.0f, CellSizeMeters) * Mathf.Clamp(SpatialTileCells, 4, 24);
		Dictionary<SpatialBatchKey, List<Transform3D>> groupedLod1 = GroupTiles(lod1, closeTileMeters);
		Dictionary<SpatialBatchKey, List<Transform3D>> groupedLod2 = GroupTiles(lod2, closeTileMeters);
		Dictionary<SpatialBatchKey, List<Transform3D>> groupedLod3 = GroupTiles(lod3, closeTileMeters);
		Dictionary<SpatialBatchKey, List<Transform3D>> groupedLod4 = GroupTiles(lod4, closeTileMeters * 2.0f);
		Dictionary<SpatialBatchKey, List<Transform3D>> groupedLod5 = GroupTiles(lod5, closeTileMeters * 4.0f);
		int maximumNodes = Mathf.Max(128, MaximumVegetationRendererNodes);
		if (UseRegionalImportedForest && EnableImportedForestCollisions)
			maximumNodes = Math.Max(maximumNodes, groupedLod1.Count);
		int closeParts = UsingAuthoredTreeMeshes ? 1 : 2;
		int requiredNodes = CountRequiredRendererNodes(groupedLod1, groupedLod2, groupedLod3, groupedLod4, groupedLod5, closeParts);
		int droppedGroups = 0;
		while (requiredNodes > maximumNodes)
		{
			bool removed = TryRemoveLastGroup(groupedLod5)
				|| TryRemoveLastGroup(groupedLod4)
				|| TryRemoveLastGroup(groupedLod3)
				|| TryRemoveLastGroup(groupedLod2)
				|| TryRemoveLastGroup(groupedLod1);
			if (!removed) break;
			droppedGroups++;
			requiredNodes = CountRequiredRendererNodes(groupedLod1, groupedLod2, groupedLod3, groupedLod4, groupedLod5, closeParts);
		}
		if (droppedGroups > 0 && !_rendererBudgetWarningIssued)
		{
			_rendererBudgetWarningIssued = true;
			GD.PushWarning($"Vegetation renderer budget dropped {droppedGroups} spatial groups; increase MaximumVegetationRendererNodes if coverage is visibly sparse.");
		}

		var uploads = new List<VegetationUpload>(requiredNodes);
		AppendVariantUploads(uploads, VegetationUploadPool.Lod1Primary, groupedLod1);
		if (!UsingAuthoredTreeMeshes) AppendVariantUploads(uploads, VegetationUploadPool.Lod1Secondary, groupedLod1);
		AppendVariantUploads(uploads, VegetationUploadPool.Lod2Primary, groupedLod2);
		if (!UsingAuthoredTreeMeshes) AppendVariantUploads(uploads, VegetationUploadPool.Lod2Secondary, groupedLod2);
		AppendVariantUploads(uploads, VegetationUploadPool.Lod3Primary, groupedLod3);
		if (!UsingAuthoredTreeMeshes) AppendVariantUploads(uploads, VegetationUploadPool.Lod3Secondary, groupedLod3);
		AppendVariantUploads(uploads, VegetationUploadPool.Lod4, groupedLod4);
		AppendVariantUploads(uploads, VegetationUploadPool.Lod5, groupedLod5);
		timer.Stop();
		LastVegetationRenderMilliseconds = timer.Elapsed.TotalMilliseconds;
		MaxVegetationRenderMilliseconds = Math.Max(MaxVegetationRenderMilliseconds, LastVegetationRenderMilliseconds);
		_vegetationRenderDirty = false;
		_vegetationRenderMovementDirty = false;
		_vegetationTilesChangedSinceRender = 0;
		return new VegetationRenderPlan
		{
			Lod1 = groupedLod1,
			Lod2 = groupedLod2,
			Lod3 = groupedLod3,
			Lod4 = groupedLod4,
			Lod5 = groupedLod5,
			Uploads = uploads,
			LogicalTreeCount = lod1.Count + lod2.Count + lod3.Count + lod4.Count + lod5.Count,
			RequiredRendererNodes = requiredNodes
		};
	}

	private static int CountRequiredRendererNodes(
		IReadOnlyDictionary<SpatialBatchKey, List<Transform3D>> lod1,
		IReadOnlyDictionary<SpatialBatchKey, List<Transform3D>> lod2,
		IReadOnlyDictionary<SpatialBatchKey, List<Transform3D>> lod3,
		IReadOnlyDictionary<SpatialBatchKey, List<Transform3D>> lod4,
		IReadOnlyDictionary<SpatialBatchKey, List<Transform3D>> lod5,
		int closeParts)
		=> (lod1.Count + lod2.Count + lod3.Count) * closeParts + lod4.Count + lod5.Count;

	private static bool TryRemoveLastGroup<TKey>(Dictionary<TKey, List<Transform3D>> groups) where TKey : notnull
	{
		if (groups.Count == 0) return false;
		TKey last = default!;
		foreach (TKey key in groups.Keys) last = key;
		return groups.Remove(last);
	}

	private static void AppendVariantUploads(
		List<VegetationUpload> destination,
		VegetationUploadPool pool,
		IReadOnlyDictionary<SpatialBatchKey, List<Transform3D>> groups)
	{
		foreach ((SpatialBatchKey key, List<Transform3D> transforms) in groups)
			destination.Add(new VegetationUpload(pool, key, 0L, transforms));
	}

	private static void AppendSimpleUploads(
		List<VegetationUpload> destination,
		VegetationUploadPool pool,
		IReadOnlyDictionary<long, List<Transform3D>> groups)
	{
		foreach ((long key, List<Transform3D> transforms) in groups)
			destination.Add(new VegetationUpload(pool, default, key, transforms));
	}

	private void ApplyVegetationUpload(VegetationUpload upload)
	{
		switch (upload.Pool)
		{
			case VegetationUploadPool.Lod1Primary:
				InstallVegetationVariantUpload(_lod1AuthoredTemplates, _lod1Trunks, _lod1TrunkTiles, upload);
				break;
			case VegetationUploadPool.Lod1Secondary:
				InstallVegetationVariantUpload(null, _lod1Canopies, _lod1CanopyTiles, upload);
				break;
			case VegetationUploadPool.Lod2Primary:
				InstallVegetationVariantUpload(_lod2AuthoredTemplates, _lod2Trunks, _lod2TrunkTiles, upload);
				break;
			case VegetationUploadPool.Lod2Secondary:
				InstallVegetationVariantUpload(null, _lod2Canopies, _lod2CanopyTiles, upload);
				break;
			case VegetationUploadPool.Lod3Primary:
				InstallVegetationVariantUpload(_lod3AuthoredTemplates, _lod3Trunks, _lod3TrunkTiles, upload);
				break;
			case VegetationUploadPool.Lod3Secondary:
				InstallVegetationVariantUpload(null, _lod3Canopies, _lod3CanopyTiles, upload);
				break;
			case VegetationUploadPool.Lod4:
				InstallVegetationVariantUpload(_lod4AuthoredTemplates, _lod4Trees, _vegetationLod4Tiles, upload);
				break;
			case VegetationUploadPool.Lod5:
				InstallVegetationVariantUpload(_lod4AuthoredTemplates, _lod5Trees, _vegetationLod5Tiles, upload);
				break;
		}
	}

	private void InstallVegetationVariantUpload(
		MultiMeshInstance3D[]? authoredTemplates,
		MultiMeshInstance3D? fallbackTemplate,
		Dictionary<SpatialBatchKey, MultiMeshInstance3D> pool,
		VegetationUpload upload)
	{
		MultiMeshInstance3D? template = authoredTemplates != null
			? authoredTemplates[Mathf.Clamp(upload.VariantKey.Variant, 0, authoredTemplates.Length - 1)]
			: fallbackTemplate;
		if (template?.Multimesh?.Mesh == null) return;
		if (!pool.TryGetValue(upload.VariantKey, out MultiMeshInstance3D? instance))
		{
			int shadowLod = TreeSunShadowLodOf(template);
			instance = CreateInstances($"{template.Name}_Tile_{upload.VariantKey.TileKey}_{upload.VariantKey.Variant}", template.Multimesh.Mesh, template.MaterialOverride,
				shadowLod >= 0, shadowLod);
			instance.ExtraCullMargin = CellSizeMeters + 32.0f;
			pool.Add(upload.VariantKey, instance);
		}
		instance.Visible = true;
		Install(instance, upload.Transforms);
	}

	private void InstallVegetationSimpleUpload(
		MultiMeshInstance3D? template,
		Dictionary<long, MultiMeshInstance3D> pool,
		VegetationUpload upload)
	{
		if (template?.Multimesh?.Mesh == null) return;
		if (!pool.TryGetValue(upload.SimpleKey, out MultiMeshInstance3D? instance))
		{
			int shadowLod = TreeSunShadowLodOf(template);
			instance = CreateInstances($"{template.Name}_Tile_{upload.SimpleKey}", template.Multimesh.Mesh, template.MaterialOverride,
				shadowLod >= 0, shadowLod);
			instance.ExtraCullMargin = CellSizeMeters + 32.0f;
			pool.Add(upload.SimpleKey, instance);
		}
		instance.Visible = true;
		Install(instance, upload.Transforms);
	}

	private void FinalizeVegetationRenderPlan(VegetationRenderPlan plan)
	{
		SetVegetationPoolTargets(_lod1TrunkTiles, plan.Lod1.Keys);
		SetVegetationPoolTargets(_lod2TrunkTiles, plan.Lod2.Keys);
		SetVegetationPoolTargets(_lod3TrunkTiles, plan.Lod3.Keys);
		if (UsingAuthoredTreeMeshes)
		{
			SetVegetationPoolTargets(_lod1CanopyTiles, Array.Empty<SpatialBatchKey>());
			SetVegetationPoolTargets(_lod2CanopyTiles, Array.Empty<SpatialBatchKey>());
			SetVegetationPoolTargets(_lod3CanopyTiles, Array.Empty<SpatialBatchKey>());
		}
		else
		{
			SetVegetationPoolTargets(_lod1CanopyTiles, plan.Lod1.Keys);
			SetVegetationPoolTargets(_lod2CanopyTiles, plan.Lod2.Keys);
			SetVegetationPoolTargets(_lod3CanopyTiles, plan.Lod3.Keys);
		}
		SetVegetationPoolTargets(_vegetationLod4Tiles, plan.Lod4.Keys);
		SetVegetationPoolTargets(_vegetationLod5Tiles, plan.Lod5.Keys);
		PruneRendererPools();
		LastInstalledTransformCount = plan.UploadedTransformCount;
		LastInstallMilliseconds = LastVegetationUploadMilliseconds;
		MaxInstallMilliseconds = Math.Max(MaxInstallMilliseconds, LastInstallMilliseconds);
	}

	private static void SetVegetationPoolTargets<TKey>(Dictionary<TKey, MultiMeshInstance3D> pool, IEnumerable<TKey> targetKeys) where TKey : notnull
	{
		var targets = new HashSet<TKey>(targetKeys);
		foreach ((TKey key, MultiMeshInstance3D instance) in pool)
		{
			bool visible = targets.Contains(key);
			instance.Visible = visible;
			if (visible || instance.Multimesh == null) continue;
			instance.Multimesh.VisibleInstanceCount = 0;
			instance.Multimesh.InstanceCount = 0;
		}
	}

	private static TileBuildResult BuildVegetationTile(TileBuildRequest request)
	{
		Stopwatch timer = Stopwatch.StartNew(); using RingTerrainSampler sampler = request.Sampler;
		// Disk-cache loads are cheap reads, not generation: they report under their own scope so "Trees build"
		// measures only tiles that were actually generated.
		if (request.AllowDiskCache)
		{
			using var loadScope = RingPerformanceTelemetry.MeasureCpu("Trees disk load", worker: true);
			if (TryLoadVegetationTile(request.DiskPath, request.Key, request.EditVersion, out VegetationTile? cached))
			{
				RingGeneratedLodCacheBudget.Touch(request.DiskPath);
				return new TileBuildResult(request.Key, cached, timer.Elapsed.TotalMilliseconds, false, true, false);
			}
		}
		using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Trees build", worker: true);
		var trees = new List<CachedTree>(request.TileCells * request.TileCells * 2);
		CanonicalVegetationCellRange(request.Key.Around, request.TileCells, request.AroundCellCount, out long startX, out long endX);
		long startZ = request.Key.Across * request.TileCells;
		long maximumCanonicalAround = Math.Max(0L, (long)Math.Ceiling(request.CircumferenceMeters) - 1L);
		ForestRegionSampler? forestRegions = request.UseRegionalForest
			? new ForestRegionSampler(request.Seed, (long)Math.Ceiling(request.CircumferenceMeters), (int)Math.Ceiling(request.RingWidth))
			: null;
		var spacingEvaluations = new Dictionary<ulong, ForestCandidateEvaluation>();
		for (long z = startZ; z < startZ + request.TileCells; z++) for (long x = startX; x < endX; x++)
		{
			if (request.Cancellation.IsCancellationRequested) return new TileBuildResult(request.Key, null, timer.Elapsed.TotalMilliseconds, true, false, false);
			for (int candidate = 0; candidate < request.Candidates; candidate++)
			{
				int salt = 11 + candidate * 17;
				double around = (x + .06 + Hash01(x, z, request.Seed, salt) * .88) * request.AroundCellMeters;
				double across = (z + .06 + Hash01(x, z, request.Seed, salt + 1) * .88) * request.AcrossCellMeters;
				if (across < 4 || across > request.RingWidth - 4) continue;
				long canonicalAround = Math.Clamp((long)Math.Round(around), 0L, maximumCanonicalAround);
				int candidateAcross = (int)Math.Round(across);
				TerrainSample sample = request.Edits.GetPresentationTerrainSample(sampler, canonicalAround, candidateAcross);
				float surface = sampler.UsesRuntimeSurfaceGeneration
					? PresentationSurfaceHeight(request.Edits, sampler, around, across)
					: PresentationSurfaceHeight(request.Edits, sampler, canonicalAround, candidateAcross);
				if (IsSurfaceWater(request.Edits, sampler, sample, surface) || (sample.Biome != TerrainBiomeId.Forest && sample.Biome != TerrainBiomeId.Grassland) || sample.SiteMask.BlocksVegetation) continue;
				if (request.UseRegionalForest && !HasStableForestRootSupport(request.Edits, sampler, canonicalAround, candidateAcross)) continue;
				if (!IsSlopeSuitable(default, sampler, request.Edits, canonicalAround, candidateAcross, surface, request.MaximumSlope)) continue;
				ForestRegionSample regional = request.UseRegionalForest
					? forestRegions!.Sample(canonicalAround, candidateAcross, sample, EstimateSlopeDegrees(sampler, request.Edits, canonicalAround, candidateAcross))
					: default;
				float chance = request.UseRegionalForest
					? regional.Density
					: sample.Biome == TerrainBiomeId.Forest ? request.ForestChance : request.GrasslandChance;
				if (Hash01(x, z, request.Seed, salt + 2) >= ApplyGroveDensity(chance, request.GroveDensity, x, z, request.Seed)) continue;
				if (request.UseRegionalForest && !WinsForestSpacingCompetition(request, forestRegions!, x, z, candidate, canonicalAround, candidateAcross, sample, regional, maximumCanonicalAround, spacingEvaluations)) continue;
				TreeSpecies species = request.UseRegionalForest ? SelectForestSpecies(regional, Hash01(x, z, request.Seed, salt + 3)) : TreeSpecies.Maple;
				int variant = SelectForestVariant(species, Hash01(x, z, request.Seed, salt + 4));
				float scale = .85f + .30f * Hash01(x, z, request.Seed, salt + 6);
				float height = request.UseRegionalForest ? request.VariantHeights[variant] * scale : 5f + 9f * Hash01(x, z, request.Seed, salt + 3);
				float width = request.UseRegionalForest ? request.VariantWidths[variant] * scale : height * (.20f + .14f * Hash01(x, z, request.Seed, salt + 4));
				float yaw = Hash01(x, z, request.Seed, salt + 5) * Mathf.Tau;
				ulong id = StableTreeId(x, z, request.Seed, candidate);
				trees.Add(new CachedTree(id, canonicalAround, candidateAcross, surface, width, height, yaw, candidate, TreeRankFromId(id), species, variant));
			}
		}
		var tile = new VegetationTile { Key = request.Key, Trees = trees, EditVersion = request.EditVersion };
		bool wrote = request.AllowDiskCache
			&& !request.Cancellation.IsCancellationRequested
			&& TryWriteVegetationTile(
				request.DiskPath,
				request.DiskBudgetRoot,
				request.DiskBudgetBytes,
				request.MinimumFreeBytes,
				tile);
		timer.Stop(); return new TileBuildResult(request.Key, tile, timer.Elapsed.TotalMilliseconds, false, false, wrote);
	}

	// A deterministic Matérn-style inhibition pass. Candidate cells are only an
	// accelerator: every contender in the one-cell halo is regenerated from its
	// canonical coordinates and a stable ID decides ties, so tile build order and
	// seam ownership cannot alter a placement. Humid cover has a smaller radius;
	// dry/open woodland retains wider gaps.
	private static bool WinsForestSpacingCompetition(in TileBuildRequest request, ForestRegionSampler regions,
		long cellX, long cellZ, int candidate, long around, int across, in TerrainSample sample,
		in ForestRegionSample regional, long maximumCanonicalAround, Dictionary<ulong, ForestCandidateEvaluation> cache)
	{
		float radius = ForestLocalSpacingMeters(sample.Moisture, regional.Density);
		ulong id = StableTreeId(cellX, cellZ, request.Seed, candidate);
		for (long z = cellZ - 1; z <= cellZ + 1; z++) for (long x = cellX - 1; x <= cellX + 1; x++)
		for (int peer = 0; peer < request.Candidates; peer++)
		{
			if (x == cellX && z == cellZ && peer == candidate) continue;
			long peerCell = PositiveModulo(x, request.AroundCellCount);
			int salt = 11 + peer * 17;
			long predictedAround = Math.Clamp((long)Math.Round((peerCell + .06 + Hash01(peerCell, z, request.Seed, salt) * .88) * request.AroundCellMeters), 0L, maximumCanonicalAround);
			int predictedAcross = (int)Math.Round((z + .06 + Hash01(peerCell, z, request.Seed, salt + 1) * .88) * request.AcrossCellMeters);
			double predictedDa = predictedAround - around;
			if (predictedDa > request.CircumferenceMeters * .5) predictedDa -= request.CircumferenceMeters;
			else if (predictedDa < -request.CircumferenceMeters * .5) predictedDa += request.CircumferenceMeters;
			double predictedDz = predictedAcross - across;
			// Nine metres is the global maximum local radius: reject before terrain,
			// edit, and climate work for the overwhelming majority of halo peers.
			if (predictedDa * predictedDa + predictedDz * predictedDz >= 81.0) continue;
			ForestCandidateEvaluation peerEvaluation = EvaluateSpacingCandidate(request, regions, peerCell, z, peer, maximumCanonicalAround, cache);
			if (!peerEvaluation.Viable) continue;
			double da = peerEvaluation.Around - around;
			if (da > request.CircumferenceMeters * .5) da -= request.CircumferenceMeters;
			else if (da < -request.CircumferenceMeters * .5) da += request.CircumferenceMeters;
			double dz = peerEvaluation.Across - across;
			float required = Mathf.Max(radius, ForestLocalSpacingMeters(peerEvaluation.Terrain.Moisture, peerEvaluation.Regional.Density));
			if (da * da + dz * dz >= required * required) continue;
			if (peerEvaluation.Id < id) return false;
		}
		return true;
	}

	private static ForestCandidateEvaluation EvaluateSpacingCandidate(in TileBuildRequest request, ForestRegionSampler regions,
		long cellX, long cellZ, int candidate, long maximumCanonicalAround, Dictionary<ulong, ForestCandidateEvaluation> cache)
	{
		ulong id = StableTreeId(cellX, cellZ, request.Seed, candidate);
		if (cache.TryGetValue(id, out ForestCandidateEvaluation cached)) return cached;
		int salt = 11 + candidate * 17;
		long around = Math.Clamp((long)Math.Round((cellX + .06 + Hash01(cellX, cellZ, request.Seed, salt) * .88) * request.AroundCellMeters), 0L, maximumCanonicalAround);
		int across = (int)Math.Round((cellZ + .06 + Hash01(cellX, cellZ, request.Seed, salt + 1) * .88) * request.AcrossCellMeters);
		if (across < 4 || across > request.RingWidth - 4) return cache[id] = default;
		TerrainSample terrain = request.Edits.GetPresentationTerrainSample(request.Sampler, around, across);
		float surface = PresentationSurfaceHeight(request.Edits, request.Sampler, around, across);
		if (IsSurfaceWater(request.Edits, request.Sampler, terrain, surface) || (terrain.Biome != TerrainBiomeId.Forest && terrain.Biome != TerrainBiomeId.Grassland) || terrain.SiteMask.BlocksVegetation) return cache[id] = default;
		if (!HasStableForestRootSupport(request.Edits, request.Sampler, around, across)) return cache[id] = default;
		float slope = EstimateSlopeDegrees(request.Sampler, request.Edits, around, across);
		if (slope > request.MaximumSlope) return cache[id] = default;
		ForestRegionSample regional = regions.Sample(around, across, terrain, slope);
		float chance = ApplyGroveDensity(regional.Density, request.GroveDensity, cellX, cellZ, request.Seed);
		if (Hash01(cellX, cellZ, request.Seed, salt + 2) >= chance) return cache[id] = default;
		return cache[id] = new ForestCandidateEvaluation(true, id, cellX, cellZ, candidate, around, across, terrain, surface, regional, chance);
	}

	private static bool HasStableForestRootSupport(RingLodSurfaceEditSnapshot edits, RingTerrainSampler sampler, long around, int across)
	{
		for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
		{
			long probeAround = around + dx; int probeAcross = across + dz;
			if (!UsesFloatPresentation(edits, sampler) && edits.TryGetEditedSurfaceHeight(probeAround, probeAcross, out int edited)
				&& edited != sampler.GetSurfaceHeight(probeAround, probeAcross)) return false;
		}
		return true;
	}

	internal static float ForestLocalSpacingMeters(float moisture, float regionalDensity)
	{
		float humid = Mathf.Clamp(moisture, 0.0f, 1.0f);
		float cover = Mathf.Clamp(regionalDensity, 0.0f, 1.0f);
		return Mathf.Lerp(9.0f, 2.5f, humid * (.35f + cover * .65f));
	}

	private static float EstimateSlopeDegrees(RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits, long around, int across)
	{
		const int probe = 2;
		float riseX = PresentationSurfaceHeight(edits, sampler, around + probe, across) - PresentationSurfaceHeight(edits, sampler, around - probe, across);
		float riseZ = PresentationSurfaceHeight(edits, sampler, around, across + probe) - PresentationSurfaceHeight(edits, sampler, around, across - probe);
		return Mathf.RadToDeg(Mathf.Atan(Mathf.Sqrt(riseX * riseX + riseZ * riseZ) / (probe * 2.0f)));
	}

	private static TreeSpecies SelectForestSpecies(in ForestRegionSample sample, float random)
	{
		float maple = sample.MapleWeight;
		float fir = maple + sample.FirWeight;
		return random < maple ? TreeSpecies.Maple : random < fir ? TreeSpecies.Fir : TreeSpecies.Pine;
	}

	private static int SelectForestVariant(TreeSpecies species, float random)
	{
		int first = species == TreeSpecies.Maple ? 0 : species == TreeSpecies.Fir ? 12 : 15;
		int count = species == TreeSpecies.Maple ? 12 : species == TreeSpecies.Fir ? 3 : 15;
		return first + Mathf.Clamp((int)(random * count), 0, count - 1);
	}

	private static bool TryLoadVegetationTile(string path, VegetationTileKey expectedKey, ulong version, out VegetationTile? tile)
	{
		tile = null; if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
		try
		{
			using var reader = new BinaryReader(File.Open(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read));
			if (reader.ReadInt32() != VegetationDiskCacheMagic
				|| reader.ReadInt32() != VegetationDiskCacheVersion
				|| reader.ReadInt64() != expectedKey.Around
				|| reader.ReadInt64() != expectedKey.Across) return false;
			int count = reader.ReadInt32(); if (count < 0 || count > MaximumTreesPerVegetationTile) return false;
			var trees = new List<CachedTree>(count);
			for (int i = 0; i < count; i++)
			{
				var tree = new CachedTree(
					reader.ReadUInt64(),
					reader.ReadInt64(),
					reader.ReadInt32(),
					reader.ReadSingle(),
					reader.ReadSingle(),
					reader.ReadSingle(),
					reader.ReadSingle(),
					reader.ReadInt32(),
					reader.ReadSingle(),
					(TreeSpecies)reader.ReadByte(),
					reader.ReadInt32());
				if (!float.IsFinite(tree.SurfaceHeight)
					|| !float.IsFinite(tree.Width)
					|| !float.IsFinite(tree.Height)
					|| !float.IsFinite(tree.Yaw)
					|| !float.IsFinite(tree.Rank)
					|| tree.Width <= 0.0f
					|| tree.Height <= 0.0f
					|| tree.Rank < 0.0f
					|| tree.Rank > 1.0f
					|| !Enum.IsDefined(tree.Species)
					|| tree.Variant < 0
					|| tree.Variant >= ImportedTreeCatalog.VariantCount
					|| (tree.Species == TreeSpecies.Maple && tree.Variant > 11)
					|| (tree.Species == TreeSpecies.Fir && (tree.Variant < 12 || tree.Variant > 14))
					|| (tree.Species == TreeSpecies.Pine && tree.Variant < 15)) return false;
				trees.Add(tree);
			}
			tile = new VegetationTile { Key = expectedKey, Trees = trees, EditVersion = version }; return true;
		}
		catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
	}
	private static bool TryWriteVegetationTile(
		string path,
		string budgetRoot,
		long budgetBytes,
		long minimumFreeBytes,
		VegetationTile tile)
	{
		if (string.IsNullOrWhiteSpace(path)) return false;
		long estimatedBytes = 32L + tile.Trees.Count * 56L;
		if (!RingGeneratedLodCacheBudget.CanWrite(budgetRoot, path, budgetBytes, minimumFreeBytes, estimatedBytes)) return false;
		string? temporary = null;
		bool recorded = false;
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			using (var writer = new BinaryWriter(File.Open(temporary, FileMode.Create, System.IO.FileAccess.Write, FileShare.None)))
			{
				writer.Write(VegetationDiskCacheMagic);
				writer.Write(VegetationDiskCacheVersion);
				writer.Write(tile.Key.Around);
				writer.Write(tile.Key.Across);
				writer.Write(tile.Trees.Count);
				foreach (CachedTree tree in tile.Trees)
				{
					writer.Write(tree.Id);
					writer.Write(tree.Around);
					writer.Write(tree.Across);
					writer.Write(tree.SurfaceHeight);
					writer.Write(tree.Width);
					writer.Write(tree.Height);
					writer.Write(tree.Yaw);
					writer.Write(tree.Candidate);
					writer.Write(tree.Rank);
					writer.Write((byte)tree.Species);
					writer.Write(tree.Variant);
				}
			}
			File.Move(temporary, path, true);
			temporary = null;
			RingGeneratedLodCacheBudget.RecordWrite(budgetRoot, path, new FileInfo(path).Length);
			recorded = true;
			return true;
		}
		catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
		finally
		{
			if (!recorded) RingGeneratedLodCacheBudget.CancelWriteReservation(budgetRoot, path);
			if (temporary != null)
			{
				try { if (File.Exists(temporary)) File.Delete(temporary); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
		}
	}

	private void PruneRendererPools()
	{
		int maximum = Mathf.Max(128, MaximumVegetationRendererNodes);
		int total = CountRendererPoolNodes();
		while (total > maximum)
		{
			bool removed = TryPruneOneInactive(_vegetationLod5Tiles)
				|| TryPruneOneInactive(_vegetationLod4Tiles)
				|| TryPruneOneInactive(_lod3CanopyTiles)
				|| TryPruneOneInactive(_lod3TrunkTiles)
				|| TryPruneOneInactive(_lod2CanopyTiles)
				|| TryPruneOneInactive(_lod2TrunkTiles)
				|| TryPruneOneInactive(_lod1CanopyTiles)
				|| TryPruneOneInactive(_lod1TrunkTiles);
			if (!removed) break;
			total--;
		}
		int active = 0;
		long bytes = 0;
		AccumulateRendererPoolMetrics(_lod1TrunkTiles, ref active, ref bytes);
		AccumulateRendererPoolMetrics(_lod1CanopyTiles, ref active, ref bytes);
		AccumulateRendererPoolMetrics(_lod2TrunkTiles, ref active, ref bytes);
		AccumulateRendererPoolMetrics(_lod2CanopyTiles, ref active, ref bytes);
		AccumulateRendererPoolMetrics(_lod3TrunkTiles, ref active, ref bytes);
		AccumulateRendererPoolMetrics(_lod3CanopyTiles, ref active, ref bytes);
		AccumulateRendererPoolMetrics(_vegetationLod4Tiles, ref active, ref bytes);
		AccumulateRendererPoolMetrics(_vegetationLod5Tiles, ref active, ref bytes);
		VegetationRendererNodeCount = CountRendererPoolNodes() + GpuTreeBatchCount;
		foreach (GpuTreeOutput output in _gpuTreeOutputs.Values) if (output.Renderer.Visible) active++;
		VegetationActiveRendererNodes = active;
		VegetationRendererEstimatedBytes = bytes + GpuTreeResidentBytes;
	}

	private int CountRendererPoolNodes()
		=> _lod1TrunkTiles.Count + _lod1CanopyTiles.Count
			+ _lod2TrunkTiles.Count + _lod2CanopyTiles.Count
			+ _lod3TrunkTiles.Count + _lod3CanopyTiles.Count
			+ _vegetationLod4Tiles.Count + _vegetationLod5Tiles.Count;

	private bool TryPruneOneInactive<TKey>(Dictionary<TKey, MultiMeshInstance3D> pool) where TKey : notnull
	{
		TKey? victim = default;
		bool found = false;
		foreach ((TKey key, MultiMeshInstance3D node) in pool)
		{
			if (node.Visible) continue;
			victim = key;
			found = true;
			break;
		}
		if (!found) return false;
		MultiMeshInstance3D instance = pool[victim!];
		if (instance.Multimesh != null)
		{
			instance.Multimesh.VisibleInstanceCount = 0;
			instance.Multimesh.InstanceCount = 0;
		}
		UnregisterTreeSunShadowRenderer(instance);
		instance.QueueFree();
		pool.Remove(victim!);
		return true;
	}

	private static void AccumulateRendererPoolMetrics<TKey>(
		IReadOnlyDictionary<TKey, MultiMeshInstance3D> pool,
		ref int active,
		ref long bytes) where TKey : notnull
	{
		foreach (MultiMeshInstance3D node in pool.Values)
		{
			if (node.Visible) active++;
			bytes += (node.Multimesh?.InstanceCount ?? 0) * 48L;
		}
	}

	internal static long CanonicalVegetationTileKey(long aroundTile, long acrossTile, long aroundTileCount) => (PositiveModulo(aroundTile, Math.Max(1, aroundTileCount)) << 32) ^ (uint)acrossTile;
	internal static bool IsVegetationTileAffectedByDirtyColumn(long tileAround, long tileAcross, long dirtyAround, long dirtyAcross, float tileMeters)
		=> Math.Abs(tileAround - FloorDiv(dirtyAround, Mathf.Max(1, Mathf.RoundToInt(tileMeters)))) <= 1 && Math.Abs(tileAcross - FloorDiv(dirtyAcross, Mathf.Max(1, Mathf.RoundToInt(tileMeters)))) <= 1;
	internal static float DeterministicTreeRank(long canonicalAround, long canonicalAcross, int seed, int candidate)
		=> TreeRankFromId(StableTreeId(canonicalAround, canonicalAcross, seed, candidate));

	internal static float VegetationLodRankGate(int lod)
		=> lod switch
		{
			<= 1 => 1.0f,
			2 => 0.85f,
			3 => 0.60f,
			4 => 0.35f,
			_ => 0.10f
		};

	internal static void CanonicalVegetationCellRange(long aroundTile, int tileCells, long aroundCellCount, out long firstCell, out long endCell)
	{
		long cells = Math.Max(1L, aroundCellCount);
		int perTile = Math.Max(1, tileCells);
		long tileCount = Math.Max(1L, (long)Math.Ceiling(cells / (double)perTile));
		long canonicalTile = PositiveModulo(aroundTile, tileCount);
		firstCell = canonicalTile * perTile;
		endCell = Math.Min(cells, firstCell + perTile);
	}

	internal static ulong VegetationCacheSettingsHash(
		long aroundCellCount,
		double circumferenceMeters,
		float cellMeters,
		float ringWidthMeters,
		int tileCells,
		int candidates,
		float forestChance,
		float grasslandChance,
		float groveDensity,
		float maximumSlope,
		bool useRegionalImportedForest = true)
	{
		ulong hash = 1469598103934665603UL;
		MixVegetationHash(ref hash, unchecked((ulong)VegetationDiskCacheVersion));
		MixVegetationHash(ref hash, unchecked((ulong)ForestRegionSampler.AlgorithmVersion));
		MixVegetationHash(ref hash, unchecked((ulong)ImportedTreeCatalog.CatalogVersion));
		MixVegetationHash(ref hash, unchecked((ulong)ImportedTreeCatalog.VariantCount));
		MixVegetationHash(ref hash, unchecked((ulong)aroundCellCount));
		MixVegetationHash(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(circumferenceMeters)));
		MixVegetationHash(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(cellMeters)));
		MixVegetationHash(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(ringWidthMeters)));
		MixVegetationHash(ref hash, unchecked((uint)tileCells));
		MixVegetationHash(ref hash, unchecked((uint)candidates));
		MixVegetationHash(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(forestChance)));
		MixVegetationHash(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(grasslandChance)));
		MixVegetationHash(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(groveDensity)));
		MixVegetationHash(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(maximumSlope)));
		MixVegetationHash(ref hash, useRegionalImportedForest ? 1UL : 0UL);
		return hash;
	}

	private static void MixVegetationHash(ref ulong hash, ulong value)
	{
		hash ^= value;
		hash *= 1099511628211UL;
	}

	private static ulong StableTreeId(long x, long z, int seed, int candidate)
	{
		ulong value = unchecked((ulong)x) * 0x9E3779B185EBCA87UL
			^ unchecked((ulong)z) * 0xC2B2AE3D27D4EB4FUL
			^ unchecked((uint)seed) * 0x165667B19E3779F9UL
			^ unchecked((uint)candidate) * 0x85EBCA77C2B2AE63UL;
		value ^= value >> 30;
		value *= 0xBF58476D1CE4E5B9UL;
		value ^= value >> 27;
		value *= 0x94D049BB133111EBUL;
		return value ^ (value >> 31);
	}

	private static float TreeRankFromId(ulong id) => (float)(id >> 40) / 16777215.0f;

	private static long AroundTileForCanonical(long canonicalAround, in VegetationLayout layout)
	{
		double around = canonicalAround % layout.CircumferenceMeters;
		if (around < 0.0) around += layout.CircumferenceMeters;
		long cell = Math.Clamp((long)Math.Floor(around / layout.AroundCellMeters), 0L, layout.AroundCellCount - 1L);
		return cell / layout.TileCells;
	}

	private static long PositiveModulo(long value, long modulus) { long result = value % modulus; return result < 0 ? result + modulus : result; }

	private void EnsureVegetationCommandLineConfiguration()
	{
		if (_vegetationCommandLineConfigured) return;
		_vegetationCommandLineConfigured = true;
		_vegetationDiskCacheDisabledByCommandLine = Array.Exists(
			OS.GetCmdlineUserArgs(),
			argument => argument.Equals("--disable-lod-disk-cache", StringComparison.OrdinalIgnoreCase));
	}
}
