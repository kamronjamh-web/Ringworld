using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

// T5 design A5, A8: the PURE scatter tile builder. It is a partial of the decoration layer only because it must reach the private tree types and spacing helpers
// (StableTreeId, Hash01, EvaluateSpacingCandidate, WinsForestSpacingCompetition, ...). It reads tree acceptance and never writes it; nothing here touches nodes,
// the scene or the main thread, and no tree function is edited. The streamer (T5-4b) calls BuildScatterRows from a worker with a sampler it owns.
public partial class RingBiomeDecorationLayer
{
	internal const int ScatterAlgorithmVersion = 1;
	internal const int ScatterCacheVersion = 1;

	private static int _scatterRowsBuildCalls;
	/// <summary>Counts BuildScatterRows calls (the feature-off check proves the review path never reaches it).</summary>
	internal static int ScatterRowsBuildCalls => Volatile.Read(ref _scatterRowsBuildCalls);

	internal readonly record struct ScatterTileKey(long Around, long Across);
	internal readonly record struct ScatterRowStat(ushort ClassId, int Candidates, int Records);

	/// <summary>The accepted tree ids of one tile, as the tile's own records give them. Scatter jobs read it and never write it.</summary>
	internal sealed class ScatterTreeSet
	{
		private readonly HashSet<ulong> _ids;
		internal ScatterTreeSet(IEnumerable<ulong> ids) { _ids = new HashSet<ulong>(ids); }
		internal int Count => _ids.Count;
		internal bool Contains(ulong id) => _ids.Contains(id);
		internal IEnumerable<ulong> Ids => _ids;
	}

	internal sealed class ScatterRowsResult
	{
		public List<ScatterRecord> Records { get; } = new();
		/// <summary>The job's habitat grid, to keep for stage C (A4).</summary>
		public RingHabitatFieldGrid? FineWindow { get; internal set; }
		public double OutcropMs, BoulderMs, CobbleMs;
		public List<ScatterRowStat> RowStats { get; } = new();
		public long FineEvaluations, CoarseEvaluations, ClimateSamples;
	}

	internal static ScatterRowsResult BuildScatterRows(ScatterBuildContext ctx, RingTerrainSampler jobSampler, ScatterTileKey key, ScatterTreeSet tileTrees,
		IReadOnlyList<ScatterRecord> earlierRecords, int rowFrom, int rowTo, int blockMask)
	{
		Interlocked.Increment(ref _scatterRowsBuildCalls);
		return ctx.Build(jobSampler, key, tileTrees, earlierRecords, rowFrom, rowTo, blockMask);
	}

	/// <summary>Review and fingerprint helper (the scatter twin of BuildImportedForestReviewTile): production layout, the tile's trees, every row and all 16 cobble blocks.</summary>
	internal static List<ScatterRecord> BuildScatterReviewTile(RingTerrainSampler sampler, long tileAround, long tileAcross, int seed, double circumference, double ringWidth,
		RingScatterClassTable table, RingLodSurfaceEditSnapshot? edits = null)
	{
		if (table.Rows.Count == 0) return new List<ScatterRecord>();
		ScatterBuildContext ctx = ScatterBuildContext.ForReview(seed, circumference, ringWidth, table, edits, sampler.WorldFeatureMask);
		var key = new ScatterTileKey(tileAround, tileAcross);
		ScatterTreeSet trees = ctx.BuildTreeSet(sampler, key);
		using RingTerrainSampler copy = sampler.CreateIndependentCopy();
		return BuildScatterRows(ctx, copy, key, trees, Array.Empty<ScatterRecord>(), 0, table.BuildOrder.Count, 0xFFFF).Records;
	}

	/// <summary>Everything a scatter job needs, captured once: the tree request parameters (the values StartVegetationTileBuilds uses, minus the per-tile key, disk and cancellation), the layout, the class table and the edit snapshot.</summary>
	internal sealed class ScatterBuildContext
	{
		internal int Seed { get; }
		internal double Circumference { get; }
		internal double RingWidth { get; }
		internal RingScatterClassTable Table { get; }
		internal RingLodSurfaceEditSnapshot Edits { get; }
		internal int WorldFeatureMask { get; }
		internal double AroundCellMeters => _layout.AroundCellMeters;
		internal long AroundCellCount => _layout.AroundCellCount;
		internal long AroundTileCount => _layout.AroundTileCount;
		internal int TileCells => _layout.TileCells;
		internal float AcrossTileMeters => _layout.AcrossTileMeters;

		private readonly TileBuildRequest _request;
		private readonly VegetationLayout _layout;

		internal ScatterBuildContext(int seed, double circumference, double ringWidth, long aroundCellCount, double aroundCellMeters, float acrossCellMeters, int tileCells, int candidates,
			float forestChance, float grasslandChance, float groveDensity, float maximumSlope, bool useRegionalForest, float[] variantWidths, float[] variantHeights,
			RingScatterClassTable table, RingLodSurfaceEditSnapshot edits, int worldFeatureMask)
		{
			IReadOnlyList<string> errors = table.Validate();
			if (errors.Count != 0) throw new ArgumentException("Invalid scatter class table: " + string.Join("; ", errors), nameof(table));
			Seed = seed; Circumference = circumference; RingWidth = ringWidth; Table = table; Edits = edits; WorldFeatureMask = worldFeatureMask;
			_layout = new VegetationLayout(aroundCellCount, aroundCellMeters, Math.Max(1L, (long)Math.Ceiling(aroundCellCount / (double)tileCells)), aroundCellMeters * tileCells,
				acrossCellMeters, acrossCellMeters * tileCells, tileCells, circumference);
			_request = new TileBuildRequest(new VegetationTileKey(0, 0), aroundCellCount, aroundCellMeters, circumference, acrossCellMeters, tileCells, candidates, ringWidth, seed,
				forestChance, grasslandChance, groveDensity, maximumSlope, useRegionalForest, variantWidths, variantHeights, edits, 0, null!, CancellationToken.None, string.Empty, string.Empty, 0, 0, false);
		}

		private ScatterBuildContext(ScatterBuildContext other, RingLodSurfaceEditSnapshot edits)
		{
			Seed = other.Seed; Circumference = other.Circumference; RingWidth = other.RingWidth; Table = other.Table; WorldFeatureMask = other.WorldFeatureMask;
			_layout = other._layout; Edits = edits; _request = other._request with { Edits = edits };
		}

		/// <summary>The same context over another edit snapshot (the streamer makes one per edit version).</summary>
		internal ScatterBuildContext WithEdits(RingLodSurfaceEditSnapshot edits) => ReferenceEquals(edits, Edits) ? this : new ScatterBuildContext(this, edits);

		/// <summary>The production layout (aroundCellMeters = Circ / ceil(Circ / 30), A9) with the review helper's tree parameters. Main thread: it loads the tree catalog.</summary>
		internal static ScatterBuildContext ForReview(int seed, double circumference, double ringWidth, RingScatterClassTable table, RingLodSurfaceEditSnapshot? edits, int worldFeatureMask)
		{
			edits ??= new RingLodSurfaceEditSnapshot(0, (long)Math.Ceiling(circumference), new Dictionary<RingTerrainColumnCoordinate, int>());
			var variants = ImportedTreeCatalog.Load();
			var widths = new float[variants.Count]; var heights = new float[variants.Count];
			foreach (ImportedTreeVariant variant in variants) { widths[variant.Index] = variant.WidthMeters; heights[variant.Index] = variant.HeightMeters; }
			long cells = Math.Max(1L, (long)Math.Ceiling(circumference / 30.0));
			return new ScatterBuildContext(seed, circumference, ringWidth, cells, circumference / cells, 30.0f, 8, 12, .72f, .20f, .70f, 32.0f, true, widths, heights, table, edits, worldFeatureMask);
		}

		/// <summary>The tile's tree ids, from BuildVegetationTile with this context's parameters (no disk).</summary>
		internal ScatterTreeSet BuildTreeSet(RingTerrainSampler sampler, ScatterTileKey key)
		{
			TileBuildRequest request = _request with { Key = new VegetationTileKey(key.Around, key.Across), Sampler = sampler.CreateIndependentCopy() };
			TileBuildResult result = BuildVegetationTile(request);
			return new ScatterTreeSet(result.Tile == null ? Array.Empty<ulong>() : result.Tile.Trees.Select(t => t.Id));
		}

		/// <summary>The A8 owner of a position given in centimetres: the tile whose production extent contains it.</summary>
		internal ScatterTileKey OwnerOf(long aroundCm, long acrossCm) => OwnerOfMetres(aroundCm / 100.0, acrossCm / 100.0);

		private ScatterTileKey OwnerOfMetres(double around, double across)
		{
			long cell = Math.Clamp((long)Math.Floor(around / _layout.AroundCellMeters), 0L, _layout.AroundCellCount - 1L);
			return new ScatterTileKey(cell / _layout.TileCells, (long)Math.Floor(across / _layout.AcrossTileMeters));
		}

		internal ScatterRowsResult Build(RingTerrainSampler jobSampler, ScatterTileKey key, ScatterTreeSet tileTrees, IReadOnlyList<ScatterRecord> earlierRecords, int rowFrom, int rowTo, int blockMask)
		{
			IReadOnlyList<RingScatterClass> order = Table.BuildOrder;
			if (rowFrom < 0 || rowTo > order.Count || rowFrom > rowTo) throw new ArgumentOutOfRangeException(nameof(rowTo));
			var result = new ScatterRowsResult();
			if (rowFrom == rowTo) return result;
			jobSampler.EnablePresentationLatticeCache();
			var job = new Job(this, jobSampler, key, tileTrees, earlierRecords, rowFrom);
			bool tileWindow = false, blockWindow = false;
			for (int r = rowFrom; r < rowTo; r++)
			{
				if (order[r].ReachM >= 2000f) continue;
				if (order[r].BlockM == 0) tileWindow = true; else if (blockMask != 0) blockWindow = true;
			}
			if (tileWindow) job.PrepareTileWindow();
			else if (blockWindow) job.PrepareBlockWindow(blockMask);
			for (int r = rowFrom; r < rowTo; r++)
			{
				RingScatterClass row = order[r];
				string scope = row.BlockM > 0 ? "Scatter build C" : row.ReachM >= 2000f ? "Scatter build O" : "Scatter build B";
				var timer = Stopwatch.StartNew();
				int before = result.Records.Count;
				using (RingPerformanceTelemetry.MeasureCpu(scope, worker: true))
				{
					int candidates = job.BuildRow(r, row, blockMask, result.Records);
					result.RowStats.Add(new ScatterRowStat(row.Id, candidates, result.Records.Count - before));
				}
				double ms = timer.Elapsed.TotalMilliseconds;
				if (row.BlockM > 0) result.CobbleMs += ms; else if (row.ReachM >= 2000f) result.OutcropMs += ms; else result.BoulderMs += ms;
				if (row.BlockM == 0) job.MarkWholeRowBuilt(row, result.Records, before);
			}
			var index = new Dictionary<ushort, int>();
			for (int i = 0; i < order.Count; i++) index[order[i].Id] = i;
			result.Records.Sort((a, b) =>
			{
				int c = index[a.ClassId].CompareTo(index[b.ClassId]);
				return c != 0 ? c : a.Id.CompareTo(b.Id);
			});
			result.FineWindow = job.Grid;
			result.FineEvaluations = job.Grid.FineEvaluations; result.CoarseEvaluations = job.Grid.CoarseEvaluations; result.ClimateSamples = job.Grid.ClimateSamples;
			return result;
		}

		/// <summary>Check (h): runs the halo tree mirror over every tree candidate of the tile and compares it with the tile's own accepted set.</summary>
		internal (int Checked, int Mismatches, List<string> Samples) CheckHaloTreeMirror(RingTerrainSampler jobSampler, ScatterTileKey key, ScatterTreeSet tileTrees)
		{
			jobSampler.EnablePresentationLatticeCache();
			var job = new Job(this, jobSampler, key, tileTrees, Array.Empty<ScatterRecord>(), 0);
			return job.CheckMirror();
		}

		/// <summary>Check (c): the accept decision of every candidate of every row inside a rectangle (centre and half extents in metres, around wraps), as seen from this tile's job. Keyed by record id.</summary>
		internal Dictionary<ulong, bool> DecideCandidatesInRectangle(RingTerrainSampler jobSampler, ScatterTileKey key, ScatterTreeSet tileTrees, double centreAround, double centreAcross, double halfAround, double halfAcross)
		{
			jobSampler.EnablePresentationLatticeCache();
			var job = new Job(this, jobSampler, key, tileTrees, Array.Empty<ScatterRecord>(), 0);
			return job.Decide(centreAround, centreAcross, halfAround, halfAcross);
		}

		private readonly record struct CandKey(ushort Class, long Cx, long Cz, int K);
		private readonly record struct Geo(ulong Id, long AroundCm, int AcrossCm, float Size)
		{
			public double A => AroundCm / 100.0;
			public double Z => AcrossCm / 100.0;
		}

		private sealed class Job
		{
			private readonly ScatterBuildContext _c;
			private readonly RingTerrainSampler _sampler;
			private readonly RingLodSurfaceEditSnapshot _edits;
			private readonly ScatterTileKey _key;
			private readonly ScatterTreeSet _trees;
			private readonly IReadOnlyList<RingScatterClass> _order;
			private readonly Dictionary<ushort, RingScatterClass> _byId = new();
			private readonly TileBuildRequest _req;
			private readonly ForestRegionSampler? _regions;
			private readonly long _maxCanon;
			private readonly long _treeStartX, _treeEndX, _treeStartZ;
			private readonly Dictionary<ulong, ForestCandidateEvaluation> _spacingEval = new();
			private readonly Dictionary<ulong, bool> _haloTrees = new();
			private readonly Dictionary<CandKey, bool> _viable = new();
			private readonly Dictionary<CandKey, bool> _accepted = new();
			private readonly Dictionary<ushort, HashSet<ulong>> _wholeBuilt = new();
			private readonly Dictionary<long, uint> _tints = new();
			private readonly List<(double A, int Z)> _treeScratch = new();
			private readonly double _circ;
			private readonly double _tileA0, _tileZ0;
			private readonly long _tileFirstCell;

			public RingHabitatFieldGrid Grid { get; }

			public Job(ScatterBuildContext c, RingTerrainSampler sampler, ScatterTileKey key, ScatterTreeSet trees, IReadOnlyList<ScatterRecord> earlier, int rowFrom)
			{
				_c = c; _sampler = sampler; _edits = c.Edits; _trees = trees; _circ = c.Circumference;
				_key = new ScatterTileKey(PositiveModulo(key.Around, c._layout.AroundTileCount), key.Across);
				_order = c.Table.BuildOrder;
				foreach (RingScatterClass row in c.Table.Rows) _byId[row.Id] = row;
				_req = c._request with { Sampler = sampler };
				_maxCanon = Math.Max(0L, (long)Math.Ceiling(_req.CircumferenceMeters) - 1L);
				_regions = _req.UseRegionalForest ? new ForestRegionSampler(_req.Seed, (long)Math.Ceiling(_req.CircumferenceMeters), (int)Math.Ceiling(_req.RingWidth)) : null;
				CanonicalVegetationCellRange(_key.Around, _req.TileCells, _req.AroundCellCount, out _treeStartX, out _treeEndX);
				_treeStartZ = _key.Across * _req.TileCells;
				_tileFirstCell = _key.Around * c._layout.TileCells;
				_tileA0 = _tileFirstCell * c._layout.AroundCellMeters;
				_tileZ0 = _key.Across * (double)c._layout.AcrossTileMeters;
				Grid = new RingHabitatFieldGrid(sampler, _edits, c.Seed, (long)Math.Ceiling(c.Circumference), (int)Math.Ceiling(c.RingWidth));
				// Rows below rowFrom are built; whole-tile ones are authoritative for in-tile Avoid checks (their records come from the caller).
				var byClass = new Dictionary<ushort, HashSet<ulong>>();
				for (int r = 0; r < rowFrom; r++) if (_order[r].BlockM == 0) byClass[_order[r].Id] = new HashSet<ulong>();
				foreach (ScatterRecord rec in earlier) if (byClass.TryGetValue(rec.ClassId, out HashSet<ulong>? set)) set.Add(rec.Id);
				foreach ((ushort id, HashSet<ulong> set) in byClass) _wholeBuilt[id] = set;
			}

			public void MarkWholeRowBuilt(RingScatterClass row, List<ScatterRecord> records, int from)
			{
				var set = new HashSet<ulong>();
				for (int i = from; i < records.Count; i++) set.Add(records[i].Id);
				_wholeBuilt[row.Id] = set;
			}

			public void PrepareTileWindow()
				=> Grid.PrepareWindow(_tileA0 - 16.0, _tileA0 + _c._layout.TileCells * _c._layout.AroundCellMeters + 16.0, _tileZ0 - 16.0, _tileZ0 + _c._layout.AcrossTileMeters + 16.0);

			public void PrepareBlockWindow(int blockMask)
			{
				double blockA = _c._layout.TileCells * _c._layout.AroundCellMeters / 4.0, blockZ = _c._layout.AcrossTileMeters / 4.0;
				int bx0 = 3, bx1 = 0, bz0 = 3, bz1 = 0;
				for (int b = 0; b < 16; b++)
				{
					if ((blockMask & (1 << b)) == 0) continue;
					bx0 = Math.Min(bx0, b & 3); bx1 = Math.Max(bx1, b & 3); bz0 = Math.Min(bz0, b >> 2); bz1 = Math.Max(bz1, b >> 2);
				}
				if (bx0 > bx1) return;
				Grid.PrepareWindow(_tileA0 + bx0 * blockA - 16.0, _tileA0 + (bx1 + 1) * blockA + 16.0, _tileZ0 + bz0 * blockZ - 16.0, _tileZ0 + (bz1 + 1) * blockZ + 16.0);
			}

			// ------------------------------------------------------------------ geometry

			private static long RingCells(RingScatterClass row, double circ) => (long)Math.Round(circ / row.CellM);
			private static long Wrap(long v, long n) { long r = v % n; return r < 0 ? r + n : r; }
			private double WrapDelta(double d) => d > _circ * .5 ? d - _circ : d < -_circ * .5 ? d + _circ : d;

			private static int Salt(RingScatterClass row, int k) => 101 + 37 * row.Id + 13 * k;

			/// <summary>cx must already be wrapped into [0, ring cells). Positions are integer centimetres; all later maths uses the centimetre values.</summary>
			private Geo Geometry(RingScatterClass row, long cx, long cz, int k)
			{
				int salt = Salt(row, k);
				int seed = _c.Seed;
				double around = (cx + .06 + .88 * Hash01(cx, cz, seed, salt)) * row.CellM;
				double across = (cz + .06 + .88 * Hash01(cx, cz, seed, salt + 1)) * row.CellM;
				double u = Hash01(cx, cz, seed, salt + 3);
				float size = (float)(row.SizeMinM * Math.Pow(row.SizeMaxM / (double)row.SizeMinM, Math.Pow(u, row.SizeExponent)));
				ulong id = StableTreeId(cx, cz, seed, unchecked((int)((uint)row.Id * 0x10000u + (uint)k)));
				return new Geo(id, (long)Math.Round(around * 100.0), (int)Math.Round(across * 100.0), size);
			}

			private bool InsideRims(in Geo g) { double z = g.Z; return !(z < 4.0 || z > _c.RingWidth - 4.0); }

			private double Dist2(in Geo a, in Geo b)
			{
				double da = WrapDelta((a.AroundCm - b.AroundCm) / 100.0), dz = (a.AcrossCm - b.AcrossCm) / 100.0;
				return da * da + dz * dz;
			}

			private ScatterTileKey Owner(in Geo g) => _c.OwnerOfMetres(g.A, g.Z);
			private bool Owned(in Geo g) => Owner(g) == _key;

			private int BlockIndex(in Geo g)
			{
				long cell = Math.Clamp((long)Math.Floor(g.A / _c._layout.AroundCellMeters), 0L, _c._layout.AroundCellCount - 1L);
				int bx = Math.Clamp((int)((cell - _tileFirstCell) * 4 / _c._layout.TileCells), 0, 3);
				int bz = Math.Clamp((int)Math.Floor((g.Z - _tileZ0) / 60.0), 0, 3);
				return bz * 4 + bx;
			}

			// ------------------------------------------------------------------ building

			public int BuildRow(int rowIndex, RingScatterClass row, int blockMask, List<ScatterRecord> records)
			{
				long n = RingCells(row, _circ);
				double a0 = _tileA0, a1 = Math.Min(_circ, _tileA0 + _c._layout.TileCells * _c._layout.AroundCellMeters);
				long cx0 = Math.Max(0L, (long)Math.Floor(a0 / row.CellM) - 1), cx1 = Math.Min(n - 1, (long)Math.Floor(a1 / row.CellM) + 1);
				long cz0 = Math.Max(0L, (long)Math.Floor(_tileZ0 / row.CellM) - 1), cz1 = (long)Math.Floor((_tileZ0 + _c._layout.AcrossTileMeters) / row.CellM) + 1;
				int candidates = 0;
				for (long cz = cz0; cz <= cz1; cz++)
					for (long cx = cx0; cx <= cx1; cx++)
						for (int k = 0; k < row.CandidatesPerCell; k++)
						{
							Geo g = Geometry(row, cx, cz, k);
							if (!InsideRims(g) || !Owned(g)) continue;
							if (row.BlockM > 0 && ((blockMask >> BlockIndex(g)) & 1) == 0) continue;
							candidates++;
							if (!Accepted(row, cx, cz, k, g)) continue;
							records.Add(MakeRecord(row, cx, cz, k, g));
						}
				return candidates;
			}

			private bool Accepted(RingScatterClass row, long cx, long cz, int k, in Geo g)
			{
				var ck = new CandKey(row.Id, cx, cz, k);
				if (_accepted.TryGetValue(ck, out bool known)) return known;
				bool ok = AcceptedCore(row, cx, cz, k, g);
				_accepted[ck] = ok;
				return ok;
			}

			/// <summary>The pure accept function: a candidate is accepted or not whichever tile asks, in whatever order (A8).</summary>
			private bool AcceptedCore(RingScatterClass row, long cx, long cz, int k, in Geo g)
			{
				if (!Viable(row, cx, cz, k, g)) return false;
				double treeR = row.TreeClearanceM + row.TreeClearanceSizeFactor * (double)g.Size;
				if (treeR > 0.0 && AnyTreeWithin(g.A, g.Z, treeR)) return false;
				if (!WinsSpacing(row, cx, cz, k, g)) return false;
				foreach (RingScatterAvoid avoid in row.Avoid)
					if (AvoidedBy(row, avoid, g)) return false;
				return true;
			}

			private bool Viable(RingScatterClass row, long cx, long cz, int k, in Geo g)
			{
				var ck = new CandKey(row.Id, cx, cz, k);
				if (_viable.TryGetValue(ck, out bool known)) return known;
				bool ok = ViableCore(row, cx, cz, k, g);
				_viable[ck] = ok;
				return ok;
			}

			private bool ViableCore(RingScatterClass row, long cx, long cz, int k, in Geo g)
			{
				if (!InsideRims(g)) return false;
				double a = g.A, z = g.Z;
				RingHabitatSample f = Grid.Sample(a, z);
				if ((f.Flags & (RingHabitatFlags.Water | RingHabitatFlags.SiteBlocked)) != 0) return false;
				double nearest = row.UsesField(RingHabitatField.NearestTreeM) ? NearestTreeM(a, z) : 50.0;
				double density = Density(row, f, nearest);
				double p = Math.Min(1.0, row.BaseDensityPerHa * density * row.CellM * (double)row.CellM / 10000.0 / row.CandidatesPerCell);
				if (!(Hash01(cx, cz, _c.Seed, Salt(row, k) + 2) < p)) return false;
				return row.BuriedDepthFraction <= 0f || BuriedBaseStaysUnderground(row, g);
			}

			/// <summary>
			/// T5-10b: the buried column of an outcrop (down to fraction x size below the root) must stay under the terrain everywhere under its footprint: the centre and 16 ring points all have terrain
			/// height at or above root height - fraction x size + clearance. Pure and deterministic (presentation surface heights through the job's lattice memo).</summary>
			private bool BuriedBaseStaysUnderground(RingScatterClass row, in Geo g)
			{
				double a = g.A, z = g.Z;
				double limit = PresentationSurfaceHeight(_edits, _sampler, a, z) - row.BuriedDepthFraction * (double)g.Size + row.BuriedClearanceM;
				double radius = row.BuriedFootprintRadius * (double)g.Size, width = _c.RingWidth;
				for (int i = 0; i < 16; i++)
				{
					double angle = i * (Math.PI / 8.0);
					double pa = a + radius * Math.Cos(angle), pz = Math.Clamp(z + radius * Math.Sin(angle), 0.0, width);
					pa -= Math.Floor(pa / _circ) * _circ;
					if (PresentationSurfaceHeight(_edits, _sampler, pa, pz) < limit) return false;
				}
				return true;
			}

			private static double Respond(in RingScatterResponse r, double value)
				=> r.Out0 + ((double)r.Out1 - r.Out0) * RingGroundSurfaceModel.SmoothStep(r.In0, r.In1, value);

			private static double FieldValue(in RingHabitatSample f, RingHabitatField field, double nearestTree) => field switch
			{
				RingHabitatField.Slope => f.SlopeDeg,
				RingHabitatField.Relief8 => f.Relief8,
				RingHabitatField.Relief64 => f.Relief64,
				RingHabitatField.Wetness => f.WetnessTotal,
				RingHabitatField.RockExposure => f.RockExposure,
				RingHabitatField.Scree => f.Scree,
				RingHabitatField.SoilDepth => f.SoilDepth,
				RingHabitatField.Canopy => f.Canopy,
				RingHabitatField.Moisture => f.Moisture,
				RingHabitatField.Temperature => f.Temperature,
				RingHabitatField.ForestWeight => f.ForestWeight,
				RingHabitatField.GrassWeight => f.GrassWeight,
				RingHabitatField.HeightAboveChannel => f.HeightAboveChannelM,
				RingHabitatField.NearestTreeM => nearestTree,
				_ => 0.0
			};

			private static double Density(RingScatterClass row, in RingHabitatSample f, double nearestTree)
			{
				double sum = 0.0;
				foreach (RingScatterTerm term in row.Terms)
				{
					double product = term.Weight;
					foreach (RingScatterResponse factor in term.Factors) product *= Respond(factor, FieldValue(f, factor.Field, nearestTree));
					sum += product;
				}
				foreach (RingScatterResponse m in row.Multipliers) sum *= Respond(m, FieldValue(f, m.Field, nearestTree));
				return sum;
			}

			/// <summary>A8 Matern II: the same class, against VIABLE peers within the hard-core distance; the smaller id wins, equal ids by (class, cx, cz, k).</summary>
			private bool WinsSpacing(RingScatterClass row, long cx, long cz, int k, in Geo g)
			{
				double maxD = row.SpacingM + row.SpacingSizeFactor * (double)row.SizeMaxM;
				if (!(maxD > 0.0)) return true;
				long ring = RingCells(row, _circ), span = (long)Math.Ceiling(maxD / row.CellM);
				for (long dz = -span; dz <= span; dz++)
					for (long dx = -span; dx <= span; dx++)
					{
						long px = Wrap(cx + dx, ring), pz = cz + dz;
						for (int k2 = 0; k2 < row.CandidatesPerCell; k2++)
						{
							if (px == cx && pz == cz && k2 == k) continue;
							Geo p = Geometry(row, px, pz, k2);
							double required = row.SpacingM + row.SpacingSizeFactor * ((double)g.Size + p.Size) * 0.5;
							if (Dist2(p, g) >= required * required) continue;
							if (!Viable(row, px, pz, k2, p)) continue;
							if (p.Id < g.Id || (p.Id == g.Id && (px, pz, k2).CompareTo((cx, cz, k)) < 0)) return false;
						}
					}
				return true;
			}

			private bool AvoidedBy(RingScatterClass row, in RingScatterAvoid avoid, in Geo g)
			{
				RingScatterClass other = _byId[avoid.ClassId];
				double maxD = avoid.ClearanceM + avoid.SizeFactor * ((double)g.Size + other.SizeMaxM) * 0.5;
				long ring = RingCells(other, _circ), span = (long)Math.Ceiling(maxD / other.CellM);
				long ex = (long)Math.Floor(g.A / other.CellM), ez = (long)Math.Floor(g.Z / other.CellM);
				for (long dz = -span; dz <= span; dz++)
					for (long dx = -span; dx <= span; dx++)
					{
						long px = Wrap(ex + dx, ring), pz = ez + dz;
						for (int k2 = 0; k2 < other.CandidatesPerCell; k2++)
						{
							Geo p = Geometry(other, px, pz, k2);
							double required = avoid.ClearanceM + avoid.SizeFactor * ((double)g.Size + p.Size) * 0.5;
							if (Dist2(p, g) >= required * required) continue;
							if (IsAcceptedEarlier(other, px, pz, k2, p)) return true;
						}
					}
				return false;
			}

			/// <summary>Inside the tile, a whole-tile earlier row is the caller's / this call's records; everywhere else (and for block rows) the pure accept function, memoised.</summary>
			private bool IsAcceptedEarlier(RingScatterClass other, long cx, long cz, int k, in Geo p)
			{
				if (other.BlockM == 0 && _wholeBuilt.TryGetValue(other.Id, out HashSet<ulong>? built) && Owned(p)) return built.Contains(p.Id);
				return Accepted(other, cx, cz, k, p);
			}

			// ------------------------------------------------------------------ trees

			private bool InTileTreeCell(long x, long z) => x >= _treeStartX && x < _treeEndX && z >= _treeStartZ && z < _treeStartZ + _req.TileCells;

			private bool TreeAccepted(long x, long z, int candidate)
				=> InTileTreeCell(x, z) ? _trees.Contains(StableTreeId(x, z, _req.Seed, candidate)) : IsHaloTreeAccepted(x, z, candidate);

			/// <summary>Collects accepted tree roots within the radius of a point. Candidates are pre-filtered by the PREDICTED position, as WinsForestSpacingCompetition does.</summary>
			private void CollectTreeRoots(double a, double z, double radius)
			{
				_treeScratch.Clear();
				double cellM = _req.AroundCellMeters, acrossCell = _req.AcrossCellMeters;
				long x0 = (long)Math.Floor((a - radius - 1.0) / cellM), x1 = (long)Math.Floor((a + radius + 1.0) / cellM);
				long z0 = (long)Math.Floor((z - radius - 1.0) / acrossCell), z1 = (long)Math.Floor((z + radius + 1.0) / acrossCell);
				for (long cz = z0; cz <= z1; cz++)
				{
					if (cz < 0) continue;
					for (long cx = x0; cx <= x1; cx++)
					{
						long peerCell = PositiveModulo(cx, _req.AroundCellCount);
						for (int cand = 0; cand < _req.Candidates; cand++)
						{
							int salt = 11 + cand * 17;
							long predictedAround = Math.Clamp((long)Math.Round((peerCell + .06 + Hash01(peerCell, cz, _req.Seed, salt) * .88) * _req.AroundCellMeters), 0L, _maxCanon);
							int predictedAcross = (int)Math.Round((cz + .06 + Hash01(peerCell, cz, _req.Seed, salt + 1) * .88) * _req.AcrossCellMeters);
							double da = WrapDelta(predictedAround - a), dz = predictedAcross - z;
							if (da * da + dz * dz >= radius * radius) continue;
							if (TreeAccepted(peerCell, cz, cand)) _treeScratch.Add((predictedAround, predictedAcross));
						}
					}
				}
			}

			private bool AnyTreeWithin(double a, double z, double radius)
			{
				CollectTreeRoots(a, z, radius);
				return _treeScratch.Count > 0;
			}

			private double NearestTreeM(double a, double z)
			{
				const double cap = 50.0;
				CollectTreeRoots(a, z, cap);
				double best = cap * cap;
				foreach ((double ta, int tz) in _treeScratch)
				{
					double da = WrapDelta(ta - a), dz = tz - z;
					best = Math.Min(best, da * da + dz * dz);
				}
				return Math.Min(cap, Math.Sqrt(best));
			}

			/// <summary>
			/// Mirrors BuildVegetationTile's acceptance for a tree candidate outside the tile (memoised per job). With regional forest: the main loop's own gates (double-coordinate water check, slope gate)
			/// plus EvaluateSpacingCandidate(...).Viable and WinsForestSpacingCompetition; otherwise the non-regional chance rule.
			/// </summary>
			internal bool IsHaloTreeAccepted(long x, long z, int candidate)
			{
				ulong id = StableTreeId(x, z, _req.Seed, candidate);
				if (_haloTrees.TryGetValue(id, out bool known)) return known;
				bool ok = MirrorTreeAcceptance(x, z, candidate);
				_haloTrees[id] = ok;
				return ok;
			}

			private bool MirrorTreeAcceptance(long x, long z, int candidate)
			{
				int salt = 11 + candidate * 17;
				double around = (x + .06 + Hash01(x, z, _req.Seed, salt) * .88) * _req.AroundCellMeters;
				double across = (z + .06 + Hash01(x, z, _req.Seed, salt + 1) * .88) * _req.AcrossCellMeters;
				if (across < 4 || across > _req.RingWidth - 4) return false;
				long canonicalAround = Math.Clamp((long)Math.Round(around), 0L, _maxCanon);
				int candidateAcross = (int)Math.Round(across);
				TerrainSample sample = _edits.GetPresentationTerrainSample(_sampler, canonicalAround, candidateAcross);
				float surface = _sampler.UsesRuntimeSurfaceGeneration
					? PresentationSurfaceHeight(_edits, _sampler, around, across)
					: PresentationSurfaceHeight(_edits, _sampler, canonicalAround, candidateAcross);
				if (IsSurfaceWater(_edits, _sampler, sample, surface) || (sample.Biome != TerrainBiomeId.Forest && sample.Biome != TerrainBiomeId.Grassland) || sample.SiteMask.BlocksVegetation) return false;
				if (_req.UseRegionalForest && !HasStableForestRootSupport(_edits, _sampler, canonicalAround, candidateAcross)) return false;
				ProbeSlopeRise(_edits, _sampler, canonicalAround, candidateAcross, out float riseX, out float riseZ);
				if (!IsSlopeAngleSuitable(riseX, riseZ, 2, _req.MaximumSlope)) return false;
				if (_req.UseRegionalForest)
				{
					ForestCandidateEvaluation evaluation = EvaluateSpacingCandidate(_req, _regions!, x, z, candidate, _maxCanon, _spacingEval);
					return evaluation.Viable
						&& WinsForestSpacingCompetition(_req, _regions!, x, z, candidate, canonicalAround, candidateAcross, sample, evaluation.Regional, _maxCanon, _spacingEval);
				}
				float chance = sample.Biome == TerrainBiomeId.Forest ? _req.ForestChance : _req.GrasslandChance;
				return !(Hash01(x, z, _req.Seed, salt + 2) >= ApplyGroveDensity(chance, _req.GroveDensity, x, z, _req.Seed));
			}

			public (int Checked, int Mismatches, List<string> Samples) CheckMirror()
			{
				int checkedCount = 0, mismatches = 0;
				var samples = new List<string>();
				for (long z = _treeStartZ; z < _treeStartZ + _req.TileCells; z++)
					for (long x = _treeStartX; x < _treeEndX; x++)
						for (int cand = 0; cand < _req.Candidates; cand++)
						{
							bool actual = _trees.Contains(StableTreeId(x, z, _req.Seed, cand));
							bool mirrored = IsHaloTreeAccepted(x, z, cand);
							checkedCount++;
							if (actual == mirrored) continue;
							mismatches++;
							if (samples.Count < 8) samples.Add($"cell ({x},{z}) candidate {cand}: tile {(actual ? "accepted" : "rejected")}, mirror {(mirrored ? "accepted" : "rejected")}");
						}
				return (checkedCount, mismatches, samples);
			}

			public Dictionary<ulong, bool> Decide(double centreAround, double centreAcross, double halfAround, double halfAcross)
			{
				var result = new Dictionary<ulong, bool>();
				foreach (RingScatterClass row in _order)
				{
					long ring = RingCells(row, _circ);
					long x0 = (long)Math.Floor((centreAround - halfAround) / row.CellM) - 1, x1 = (long)Math.Floor((centreAround + halfAround) / row.CellM) + 1;
					long z0 = Math.Max(0L, (long)Math.Floor((centreAcross - halfAcross) / row.CellM) - 1), z1 = (long)Math.Floor((centreAcross + halfAcross) / row.CellM) + 1;
					for (long cz = z0; cz <= z1; cz++)
						for (long cx = x0; cx <= x1; cx++)
							for (int k = 0; k < row.CandidatesPerCell; k++)
							{
								long wx = Wrap(cx, ring);
								Geo g = Geometry(row, wx, cz, k);
								if (!InsideRims(g)) continue;
								if (Math.Abs(WrapDelta(g.A - centreAround)) > halfAround || Math.Abs(g.Z - centreAcross) > halfAcross) continue;
								result[g.Id] = Accepted(row, wx, cz, k, g);
							}
				}
				return result;
			}

			// ------------------------------------------------------------------ records

			private ScatterRecord MakeRecord(RingScatterClass row, long cx, long cz, int k, in Geo g)
			{
				int salt = Salt(row, k), seed = _c.Seed;
				double a = g.A, z = g.Z;
				float surface = PresentationSurfaceHeight(_edits, _sampler, a, z);
				(double gx, double gz) = Grid.Gradient(a, z);
				(double ux, double uy, double uz) = AlignedUp(row, gx, gz, cx, cz, seed, salt);
				int mesh = row.MeshFirst + Math.Min(row.MeshCount - 1, (int)Math.Floor(Hash01(cx, cz, seed, salt + 5) * row.MeshCount));
				float yaw = (float)(Hash01(cx, cz, seed, salt + 4) * Math.Tau);
				return new ScatterRecord(g.Id, g.AroundCm, g.AcrossCm, surface, row.Id, (ushort)mesh, g.Size, yaw, ScatterRecord.EncodeUpOct(ux, uy, uz), Tint(a, z));
			}

			private static (double X, double Y, double Z) Normalize(double x, double y, double z)
			{
				double l = Math.Sqrt(x * x + y * y + z * z);
				return l > 1e-12 ? (x / l, y / l, z / l) : (0.0, 1.0, 0.0);
			}

			private static (double X, double Y, double Z) AlignedUp(RingScatterClass row, double gx, double gz, long cx, long cz, int seed, int salt)
			{
				var normal = Normalize(-gx, 1.0, -gz);
				switch (row.Alignment)
				{
					case RingScatterAlignment.LyingAlongSlope: return normal;
					case RingScatterAlignment.SlopeAligned:
					{
						double t = Math.Clamp(row.AlignFactor, 0f, 1f);
						var up = Normalize(normal.X * t, 1.0 + (normal.Y - 1.0) * t, normal.Z * t);
						double tilt = Hash01(cx, cz, seed, salt + 7) * row.RandomTiltDeg * Math.PI / 180.0, az = Hash01(cx, cz, seed, salt + 6) * Math.Tau;
						if (tilt == 0.0) return up;
						// An orthonormal basis (e1, e2) around up, then lean by tilt towards the azimuth.
						var reference = Math.Abs(up.X) < 0.9 ? (X: 1.0, Y: 0.0, Z: 0.0) : (X: 0.0, Y: 0.0, Z: 1.0);
						var e1 = Normalize(reference.Y * up.Z - reference.Z * up.Y, reference.Z * up.X - reference.X * up.Z, reference.X * up.Y - reference.Y * up.X);
						var e2 = (X: up.Y * e1.Z - up.Z * e1.Y, Y: up.Z * e1.X - up.X * e1.Z, Z: up.X * e1.Y - up.Y * e1.X);
						double ca = Math.Cos(az), sa = Math.Sin(az), ct = Math.Cos(tilt), st = Math.Sin(tilt);
						return Normalize(up.X * ct + (e1.X * ca + e2.X * sa) * st, up.Y * ct + (e1.Y * ca + e2.Y * sa) * st, up.Z * ct + (e1.Z * ca + e2.Z * sa) * st);
					}
					default: return (0.0, 1.0, 0.0);
				}
			}

			// RingScaledFarViewport's default LowTerrainColor / HighTerrainColor: instance exports there, not reachable from a static context. They only feed the water fallback of ResolveMacroTextureColor.
			private static readonly Color LowGround = new(0.12f, 0.19f, 0.12f, 1.0f);
			private static readonly Color HighGround = new(0.29f, 0.27f, 0.20f, 1.0f);

			private uint Tint(double a, double z)
			{
				long coarseCount = (long)Math.Ceiling(_c.Circumference) / 32;
				int lastC = ((int)Math.Ceiling(_c.RingWidth) - 1) / 32;
				long I = Wrap((long)Math.Round(a / 32.0), coarseCount);
				int J = Math.Clamp((int)Math.Round(z / 32.0), 0, lastC);
				long key = (I << 20) | (uint)J;
				if (_tints.TryGetValue(key, out uint cached)) return cached;
				_edits.GetPresentationTerrainSampleAndHeight(_sampler, I * 32, J * 32, out TerrainSample sample, out float height);
				Color c = RingTerrainVisuals.ResolveMacroTextureColor(sample, Mathf.RoundToInt(height), _sampler, LowGround, HighGround);
				return _tints[key] = ScatterRecord.PackBaseTint(c.R, c.G, c.B);
			}
		}
	}
}
