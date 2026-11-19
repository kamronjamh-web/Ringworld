using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Vegetation.Understory;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

// T5 design A6/A7: the runtime scatter streamer. It lives beside the tree streamer and reads tree tiles (their ids) but never writes them. `_scatter` exists only in worlds whose
// feature mask has bit 4 (scatter.rocks); everywhere else the hooks in TreeStreaming.cs are null checks.
public partial class RingBiomeDecorationLayer
{
	internal const int MaximumScatterJobsInFlight = 2;

	/// <summary>Multiplies every row's reach: the streamer's due radii, the layer's bands and the far speckle's start (T5-9, the Vegetation tab's "Scatter distance scale"). Never changes placement.</summary>
	internal float ScatterDistanceScale { get; set; } = 1f;
	/// <summary>The rank gate of every rock batch and the far speckle's density (the Vegetation tab's "Rock density"); applies at the next cull, no rebuild.</summary>
	internal float ScatterRockDensity { get; set; } = 1f;
	/// <summary>Scales the per-LOD scatter budgets (the graphics preset's cap scale; no menu row).</summary>
	internal float ScatterCapScale { get; set; } = 1f;

	/// <summary>The vegetation settings apply (T5-9). Stored here and read by the next scatter pump; in a world without scatter.rocks nothing reads them and no global is written.</summary>
	internal void ApplyScatterSettings(float rockDensity, float distanceScale, float capScale)
	{
		ScatterRockDensity = rockDensity;
		ScatterDistanceScale = distanceScale;
		ScatterCapScale = capScale;
	}

	/// <summary>The rank gate of the sapling batches (the Vegetation tab's "Understory density"); applies at the next cull, no rebuild. Only the understory layer reads it.</summary>
	internal float UnderstoryDensity { get; set; } = 1f;
	/// <summary>The rank gate of the dead-wood batches and the filter of the dead-wood colliders (the Vegetation tab's "Dead wood density"); no rebuild.</summary>
	internal float DeadWoodDensity { get; set; } = 1f;

	internal void ApplyUnderstorySettings(float understoryDensity, float deadWoodDensity)
	{
		UnderstoryDensity = Math.Clamp(understoryDensity, 0f, 1f);
		DeadWoodDensity = Math.Clamp(deadWoodDensity, 0f, 1f);
	}

	private ScatterStreamer? _scatter;
	private RingScatterLayer? _scatterLayer;
	private RingUnderstoryLayer? _understoryLayer;
	private RingTerrainSampler? _scatterSampler;
	private RingLodSurfaceEditSnapshot? _scatterEdits;
	private ulong _scatterEditsVersion = ulong.MaxValue;

	public int ScatterTilesResident => _scatter?.TilesResident ?? 0;
	public long ScatterRecordsResident => _scatter?.RecordsResident ?? 0;
	public int ScatterJobsInFlight => _scatter?.JobsInFlight ?? 0;
	public long ScatterResultsDiscarded => _scatter?.ResultsDiscarded ?? 0;
	/// <summary>Scatter state, kept out of the tree cache budget.</summary>
	public long ScatterMemoryBytes => _scatter?.MemoryBytes ?? 0;
	/// <summary>Bumped on every install or drop; T5-7b and T5-8 compare it to know when to re-read the records.</summary>
	public long ScatterRecordEpoch => _scatter?.RecordEpoch ?? 0;

	/// <summary>Every resident scatter record, tile by tile (tile key order) then in record order.</summary>
	internal void CopyResidentScatterRecords(List<ScatterRecord> into) => _scatter?.CopyRecords(into);

	// ------------------------------------------------------------------ one-line hook bodies (TreeStreaming.cs calls these)

	/// <summary>TEST ONLY (T5-10, tools/ScatterDebugBridge.cs): record per-tile build times (the tree tile's own build ms and the scatter stage ms) in the streamer. Off in every normal run.</summary>
	internal static bool ScatterTileTiming { get; set; }

	private void ScatterTileResident(VegetationTileKey key, VegetationTile tile, double treeMs = -1.0, bool treeDiskHit = true)
	{
		if (_scatter == null) return;
		var ids = new List<ulong>(tile.Trees.Count);
		foreach (CachedTree tree in tile.Trees) ids.Add(tree.Id);
		_scatter.OnTreeTileResident(new ScatterTileKey(key.Around, key.Across), ids, treeMs, treeDiskHit);
	}

	private void ScatterTileEvicted(VegetationTileKey key) => _scatter?.OnTreeTileEvicted(new ScatterTileKey(key.Around, key.Across));

	private void ScatterTilesInvalidated(HashSet<VegetationTileKey> keys)
	{
		if (_scatter == null) return;
		_scatter.OnTreeTilesInvalidated(keys.Select(k => new ScatterTileKey(k.Around, k.Across)).ToList());
	}

	private void ShutdownScatter()
	{
		if (_scatterLayer != null)
		{
			// Always queued: this runs from _ExitTree, when the layer's parent may be busy removing children (a direct Free would call remove_child on it).
			if (GodotObject.IsInstanceValid(_scatterLayer)) _scatterLayer.QueueFree();
			_scatterLayer = null;
		}
		if (_understoryLayer != null)
		{
			if (GodotObject.IsInstanceValid(_understoryLayer)) _understoryLayer.QueueFree();
			_understoryLayer = null;
		}
		_scatter?.Shutdown();
		_scatter = null;
		_scatterSampler = null;
		_scatterEdits = null;
		_scatterEditsVersion = ulong.MaxValue;
	}

	/// <summary>The per-frame pump, and the creation point: the streamer is made once, when the layer first sees a sampler whose mask has bit 4.</summary>
	private void PumpScatter(RingTerrainSampler sampler)
	{
		if (_explorer == null || _grid == null) return;
		if (_scatter != null && !ReferenceEquals(_scatterSampler, sampler)) ShutdownScatter();
		if (_scatter == null)
		{
			if (ReferenceEquals(_scatterSampler, sampler)) return; // already decided for this sampler
			_scatterSampler = sampler;
			RingLodSurfaceEditSnapshot first = _smoothTerrain?.CaptureLodSurfaceEditSnapshot() ?? _grid.CaptureLodSurfaceEditSnapshot();
			_scatter = CreateScatterStreamer(sampler, first);
			if (_scatter == null) return;
			_scatterLayer = CreateScatterLayerFor(sampler, BuildScatterLayer);
			AddScatterLayerToParent(_scatterLayer);
			_understoryLayer = CreateUnderstoryLayerFor(sampler, BuildUnderstoryLayer);
			AddScatterLayerToParent(_understoryLayer);
			_scatterEdits = first; _scatterEditsVersion = first.Version;
			foreach ((VegetationTileKey key, VegetationTile tile) in _vegetationTiles) ScatterTileResident(key, tile);
		}
		if (_scatterEdits == null || _scatterEditsVersion != _vegetationEditVersion)
		{
			_scatterEdits = _smoothTerrain?.CaptureLodSurfaceEditSnapshot() ?? _grid.CaptureLodSurfaceEditSnapshot();
			_scatterEditsVersion = _scatterEdits.Version;
		}
		RingCanonicalPosition focus = _explorer.CanonicalPosition;
		float height = (float)(focus.VerticalMeters - PresentationSurfaceHeight(_scatterEdits, sampler, focus.AroundMeters, focus.AcrossMeters));
		// The look settings (T5-9): only worlds with scatter.rocks reach here, so everywhere else the ground world global stays (x, y, 0, 0).
		if (_scatterLayer != null) { _scatterLayer.RockDensity = ScatterRockDensity; _scatterLayer.CapScale = ScatterCapScale; }
		if (_understoryLayer != null) { _understoryLayer.CapScale = ScatterCapScale; _understoryLayer.UnderstoryDensity = UnderstoryDensity; _understoryLayer.DeadWoodDensity = DeadWoodDensity; }
		RingGroundSurfaceGlobals.PublishScatterLook(ScatterRockDensity, ScatterDistanceScale);
		_scatter.DistanceScale = ScatterDistanceScale;
		_scatter.Pump(focus.AroundMeters, focus.AcrossMeters, height, _scatterEdits, _scatterEdits.Version);
	}

	/// <summary>Null unless the world has scatter.rocks (bit 4). Builds the context from the live layout and the tree parameters StartVegetationTileBuilds uses.</summary>
	private ScatterStreamer? CreateScatterStreamer(RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits)
		=> CreateScatterStreamerFor(sampler, () => BuildScatterStreamer(sampler, edits));

	/// <summary>The creation decision: null (and nothing built) unless the sampler's mask has bit 4 or bit 6 (T6 understory rows).</summary>
	internal static ScatterStreamer? CreateScatterStreamerFor(RingTerrainSampler sampler, Func<ScatterStreamer> make)
		=> RingHabitatFieldGrid.IsEnabled(sampler.WorldFeatureMask) ? make() : null;

	/// <summary>The rock layer: null (and nothing built) unless the sampler's mask has bit 4. A bit 6 only world streams sapling records and draws none (the renderer is T6-4b).</summary>
	internal static RingScatterLayer? CreateScatterLayerFor(RingTerrainSampler sampler, Func<RingScatterLayer> make)
		=> (sampler.WorldFeatureMask & (1 << RingHabitatFieldGrid.ScatterRocksBit)) != 0 ? make() : null;

	/// <summary>The understory layer (T6-4b): null (and nothing built) unless the sampler's mask has bit 6 (vegetation.forest_structure).</summary>
	internal static RingUnderstoryLayer? CreateUnderstoryLayerFor(RingTerrainSampler sampler, Func<RingUnderstoryLayer> make)
		=> (sampler.WorldFeatureMask & (1 << RingHabitatFieldGrid.ScatterVegetationBit)) != 0 ? make() : null;

	private RingScatterLayer BuildScatterLayer() => new()
	{
		Name = "ScatterLayer",
		Host = BuildScatterHost(),
	};

	private RingUnderstoryLayer BuildUnderstoryLayer() => new()
	{
		Name = "UnderstoryLayer",
		Host = BuildScatterHost(),
	};

	private ScatterLayerHost BuildScatterHost() => new()
	{
		Geometry = () => _grid?.CaptureLodGeometrySnapshot(),
		Focus = () => _explorer?.CanonicalPosition,
		Epoch = () => ScatterRecordEpoch,
		CopyRecords = CopyResidentScatterRecords,
		RingAcrossWidthMeters = () => _grid?.RingAcrossWidthMeters ?? 0.0,
		DistanceScale = () => ScatterDistanceScale,
		Sun = () => { ResolveTreeSunLight(); return _treeSunLight; },
		Altitude = () => _explorer?.CurrentAltitudeAboveTerrainMeters ?? 0f,
		RegisterTreeMaterial = RegisterTreeLightingMaterial,
		SimplifyDistantMaterials = () => SimplifyDistantTreeMaterials,
	};

	/// <summary>A sibling of this layer, never a descendant (the landscape harness counts everything under the decoration layer as trees).</summary>
	private void AddScatterLayerToParent(Node3D? layer)
	{
		Node? parent = GetParent();
		if (layer == null || parent == null) return;
		Callable.From(() =>
		{
			if (GodotObject.IsInstanceValid(layer) && !layer.IsQueuedForDeletion() && GodotObject.IsInstanceValid(parent)) parent.AddChild(layer);
		}).CallDeferred();
	}

	private ScatterStreamer BuildScatterStreamer(RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits)
	{
		EnsureVegetationCommandLineConfiguration();
		VegetationLayout layout = CaptureVegetationLayout();
		int candidates = Mathf.Clamp(CandidatesPerCell, 1, 12);
		RingScatterClassTable table = RingScatterClassTable.Production;
		var ctx = new ScatterBuildContext(sampler.Seed, layout.CircumferenceMeters, _grid!.RingAcrossWidthMeters, layout.AroundCellCount, layout.AroundCellMeters, layout.AcrossCellMeters, layout.TileCells,
			candidates, ForestSpawnChance, GrasslandSpawnChance, GroveDensity, MaximumSlopeDegrees, UseRegionalImportedForest,
			UseRegionalImportedForest ? CaptureImportedVariantWidths() : Array.Empty<float>(), UseRegionalImportedForest ? CaptureImportedVariantHeights() : Array.Empty<float>(), table, edits, sampler.WorldFeatureMask);
		ScatterDiskConfig? disk = null;
		string activeWorld = ProjectSettings.GetSetting("ringworld/runtime/active_world_directory", "").AsString();
		if (!string.IsNullOrWhiteSpace(activeWorld))
		{
			ulong treeSettings = VegetationCacheSettingsHash(layout.AroundCellCount, layout.CircumferenceMeters, layout.AcrossCellMeters, (float)_grid.RingAcrossWidthMeters, layout.TileCells, candidates,
				ForestSpawnChance, GrasslandSpawnChance, GroveDensity, MaximumSlopeDegrees, UseRegionalImportedForest);
			disk = new ScatterDiskConfig(ResolveVegetationLodCacheRoot(activeWorld), RingScatterTileFile.Identity(sampler, ctx.Table, treeSettings),
				Math.Max(0, VegetationDiskCacheBudgetMiB) * 1024L * 1024L, Math.Max(0, VegetationDiskCacheMinimumFreeMiB) * 1024L * 1024L);
		}
		return new ScatterStreamer(ctx, sampler.CreateIndependentCopy(), disk, WorldGenerationLedger.Production,
			() => EnableVegetationDiskCache && !_vegetationDiskCacheDisabledByCommandLine && VegetationDiskCacheBudgetMiB > 0,
			(key, snapshot) => TileTouchesEditedColumns(new VegetationTileKey(key.Around, key.Across), snapshot, CaptureVegetationLayout()));
	}

	internal sealed record ScatterDiskConfig(string Root, string Identity, long BudgetBytes, long MinimumFreeBytes);

	internal sealed class ScatterTileState
	{
		public int BuiltRows;
		public ushort CobbleBlockMask;
		public List<ScatterRecord> Records = new();
		/// <summary>Not retained: the grid is bound to its job's sampler, which the job disposes. Jobs make a fresh grid; the records are identical by the grid's purity (T5-3).</summary>
		public RingHabitatFieldGrid? FineWindow;
		public ulong EditVersion;
		public long Bytes;
		public required ScatterTreeSet Trees;
		public bool InFlight, DiskTried;
	}

	/// <summary>A plain C# class (no Node) so it can be driven headlessly. Main thread only, except for its jobs.</summary>
	internal sealed partial class ScatterStreamer
	{
		private ScatterBuildContext _ctx;
		private readonly ScatterBuildContext _baseCtx;
		private readonly RingTerrainSampler _template;
		private readonly ScatterDiskConfig? _disk;
		private readonly WorldGenerationLedger _ledger;
		private readonly Func<bool> _diskEnabled;
		private readonly Func<ScatterTileKey, RingLodSurfaceEditSnapshot, bool>? _touchesEdits;
		private readonly Dictionary<ScatterTileKey, ScatterTileState> _tiles = new();
		private readonly Dictionary<ScatterTileKey, StreamJob> _jobs = new();
		private ulong _version;
		private bool _haveVersion;
		private bool _shutdown;

		public float DistanceScale { get; set; } = 1f;
		public int MaxJobs { get; set; } = MaximumScatterJobsInFlight;
		public int JobsInFlight => _jobs.Count;
		public int MaxJobsInFlightObserved { get; private set; }
		public int JobsStarted { get; private set; }
		public long ResultsDiscarded { get; private set; }
		public long RecordEpoch { get; private set; }
		public int DiskLoads { get; private set; }
		public int DiskWrites { get; private set; }
		public int TilesKnown => _tiles.Count;
		/// <summary>Tiles still due for work after the latest pump (0 once everything in reach is built); -1 before the first pump.</summary>
		public int DueRemaining { get; private set; } = -1;

		/// <summary>T5-10 debug record of one tile: the tree tile's own build ms (-1 when unknown or a disk hit) and the scatter stage worker ms (including the habitat grid), summed over its jobs.</summary>
		internal sealed class TileTiming
		{
			public double TreeMs = -1; public bool TreeDiskHit;
			public double OMs, BMs, CMs;
			public int Jobs, ScatterDiskLoads;
			/// <summary>One entry per cobble job: its worker ms and the number of 60 m blocks it built.</summary>
			public readonly List<(double Ms, int Blocks)> CJobs = new();
		}
		internal readonly Dictionary<ScatterTileKey, TileTiming> Timings = new();

		public ScatterStreamer(ScatterBuildContext ctx, RingTerrainSampler templateCopy, ScatterDiskConfig? disk, WorldGenerationLedger ledger, Func<bool>? diskEnabled = null,
			Func<ScatterTileKey, RingLodSurfaceEditSnapshot, bool>? touchesEditedColumns = null)
		{
			_ctx = _baseCtx = ctx;
			_template = templateCopy; _disk = disk; _ledger = ledger; _diskEnabled = diskEnabled ?? (() => true); _touchesEdits = touchesEditedColumns;
		}

		public int TilesResident { get { int n = 0; foreach (ScatterTileState s in _tiles.Values) if (s.BuiltRows > 0) n++; return n; } }
		public long RecordsResident { get { long n = 0; foreach (ScatterTileState s in _tiles.Values) n += s.Records.Count; return n; } }
		public long MemoryBytes { get { long n = 0; foreach (ScatterTileState s in _tiles.Values) n += s.Bytes; return n; } }

		public ScatterTileState? StateOf(ScatterTileKey key) => _tiles.TryGetValue(key, out ScatterTileState? s) ? s : null;

		private static long BytesOf(ScatterTileState s) => 64L + s.Records.Count * 44L + s.Trees.Count * 16L;

		// ------------------------------------------------------------------ tree tile hooks

		public void OnTreeTileResident(ScatterTileKey key, IEnumerable<ulong> treeIds, double treeMs = -1.0, bool treeDiskHit = true)
		{
			if (_shutdown) return;
			if (ScatterTileTiming) Timings[key] = new TileTiming { TreeMs = treeDiskHit ? -1.0 : treeMs, TreeDiskHit = treeDiskHit };
			bool had = _tiles.TryGetValue(key, out ScatterTileState? old);
			var state = new ScatterTileState { Trees = new ScatterTreeSet(treeIds), EditVersion = _version };
			state.Bytes = BytesOf(state);
			_tiles[key] = state;
			if (had && old!.Records.Count > 0) RecordEpoch++;
		}

		public void OnTreeTileEvicted(ScatterTileKey key)
		{
			if (_tiles.Remove(key, out ScatterTileState? old) && old.Records.Count > 0) RecordEpoch++;
		}

		public void OnTreeTilesInvalidated(IEnumerable<ScatterTileKey> keys)
		{
			foreach (ScatterTileKey key in keys) OnTreeTileEvicted(key);
		}

		/// <summary>Cancels in-flight jobs and keeps the state (the layer's suspend).</summary>
		public void CancelJobs() { foreach (StreamJob job in _jobs.Values) job.Cancellation.Cancel(); }

		public void CopyRecords(List<ScatterRecord> into)
		{
			foreach (ScatterTileKey key in _tiles.Keys.OrderBy(k => k.Around).ThenBy(k => k.Across)) into.AddRange(_tiles[key].Records);
		}

		/// <summary>True when no job is in flight and no tile is due for work from the given focus (test helper).</summary>
		public bool IsIdle => _jobs.Count == 0;
		public bool AllJobsCompleted => _jobs.Values.All(j => j.Task.IsCompleted);

		// ------------------------------------------------------------------ pump

		public void Pump(double focusAround, double focusAcross, float heightAboveGroundM, RingLodSurfaceEditSnapshot edits, ulong editVersion)
		{
			if (_shutdown || _ctx.Table.BuildOrder.Count == 0) return;
			if (!_haveVersion || editVersion != _version)
			{
				_haveVersion = true; _version = editVersion;
				_ctx = _baseCtx.WithEdits(edits);
			}
			Install(editVersion);
			DropOutOfRange(focusAround, focusAcross);
			if (_jobs.Count >= MaxJobs) { DueRemaining = -1; return; }
			var due = new List<(double Distance, ScatterTileKey Key, ScatterTileState State, int Rows, int Mask)>();
			foreach ((ScatterTileKey key, ScatterTileState state) in _tiles)
			{
				if (state.InFlight || _jobs.ContainsKey(key)) continue;
				(int rows, int mask) = DueFor(key, focusAround, focusAcross, heightAboveGroundM);
				if (rows > state.BuiltRows || (mask & ~state.CobbleBlockMask) != 0) due.Add((TileDistance(key, focusAround, focusAcross), key, state, rows, mask));
			}
			due.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Key.Around != b.Key.Around ? a.Key.Around.CompareTo(b.Key.Around) : a.Key.Across.CompareTo(b.Key.Across));
			int startedNow = 0;
			foreach (var item in due)
			{
				if (_jobs.Count >= MaxJobs) break;
				StartJob(item.Key, item.State, item.Rows, item.Mask, edits, editVersion);
				startedNow++;
			}
			DueRemaining = due.Count - startedNow;
		}

		private void Install(ulong currentVersion)
		{
			List<ScatterTileKey>? finished = null;
			foreach ((ScatterTileKey key, StreamJob job) in _jobs) if (job.Task.IsCompleted) (finished ??= new()).Add(key);
			if (finished == null) return;
			foreach (ScatterTileKey key in finished)
			{
				StreamJob job = _jobs[key];
				_jobs.Remove(key);
				job.Cancellation.Dispose();
				job.State.InFlight = false;
				JobResult? result = job.Task.IsFaulted || job.Task.IsCanceled ? null : job.Task.Result;
				bool current = _tiles.TryGetValue(key, out ScatterTileState? live) && ReferenceEquals(live, job.State);
				// A stale result (the tile left, was rebuilt, or the edit version moved on) is discarded and counted; nothing of it is installed.
				if (result == null || result.Failed || !current || job.Version != currentVersion)
				{
					ResultsDiscarded++;
					if (current && result != null && !result.Failed) job.State.DiskTried = false;
					continue;
				}
				job.State.Records = result.Records;
				job.State.BuiltRows = result.BuiltRows;
				job.State.CobbleBlockMask = result.BlockMask;
				job.State.EditVersion = job.Version;
				job.State.Bytes = BytesOf(job.State);
				if (ScatterTileTiming && Timings.TryGetValue(key, out TileTiming? timing))
				{
					timing.OMs += result.OMs; timing.BMs += result.BMs; timing.CMs += result.CMs; timing.Jobs++;
					if (result.CBlocks > 0) timing.CJobs.Add((result.CMs, result.CBlocks));
					if (result.DiskLoaded) timing.ScatterDiskLoads++;
				}
				if (result.DiskLoaded) DiskLoads++;
				if (result.DiskWritten) DiskWrites++;
				RecordEpoch++;
			}
		}

		/// <summary>Drops the rows from the first one a tile is more than 300 m outside of, and their records.</summary>
		private void DropOutOfRange(double focusAround, double focusAcross)
		{
			IReadOnlyList<RingScatterClass> order = _ctx.Table.BuildOrder;
			foreach ((ScatterTileKey key, ScatterTileState state) in _tiles)
			{
				if (state.BuiltRows == 0 || state.InFlight) continue;
				int keep = KeepRows(key, focusAround, focusAcross);
				if (keep >= state.BuiltRows) continue;
				var dropped = new HashSet<ushort>();
				for (int r = keep; r < state.BuiltRows; r++) dropped.Add(order[r].Id);
				state.Records = state.Records.Where(rec => !dropped.Contains(rec.ClassId)).ToList();
				state.BuiltRows = keep;
				bool anyBlockRow = false;
				for (int r = 0; r < keep; r++) if (order[r].BlockM > 0) anyBlockRow = true;
				if (!anyBlockRow) state.CobbleBlockMask = 0;
				state.FineWindow = null;
				state.Bytes = BytesOf(state);
				RecordEpoch++;
			}
		}

		public void Shutdown()
		{
			if (_shutdown) return;
			_shutdown = true;
			foreach (StreamJob job in _jobs.Values) job.Cancellation.Cancel();
			Task[] tasks = _jobs.Values.Select(j => (Task)j.Task).ToArray();
			try { if (tasks.Length > 0) Task.WaitAll(tasks, 1500); }
			catch (AggregateException) { }
			foreach (StreamJob job in _jobs.Values) job.Cancellation.Dispose();
			bool allDone = _jobs.Values.All(j => j.Task.IsCompleted);
			_jobs.Clear();
			_tiles.Clear();
			if (allDone) _template.Dispose();
		}
	}
}
