using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

// T5 design A6 "Stages": the due logic and the worker job of the scatter streamer. Plain C#: no nodes, no scene access; the job touches only its own sampler copy and immutable inputs.
public partial class RingBiomeDecorationLayer
{
	internal sealed partial class ScatterStreamer
	{
		/// <summary>Margin added to a row's reach: a tile is due when its centre is within ReachM x scale + 300 m of the focus.</summary>
		internal const double DueMarginM = 300.0;
		internal const double BlockDueMarginM = 50.0;
		internal const double CobbleMaximumHeightM = 60.0;
		private const double TileRadiusM = 170.0;

		private double WrapDelta(double d) { double c = _ctx.Circumference; return d > c * .5 ? d - c : d < -c * .5 ? d + c : d; }

		private (double A, double Z) TileCentre(ScatterTileKey key)
			=> ((key.Around * (double)_ctx.TileCells + _ctx.TileCells * .5) * _ctx.AroundCellMeters, key.Across * (double)_ctx.AcrossTileMeters + _ctx.AcrossTileMeters * .5);

		private double TileDistance(ScatterTileKey key, double focusAround, double focusAcross)
		{
			(double a, double z) = TileCentre(key);
			double da = WrapDelta(a - focusAround), dz = z - focusAcross;
			return Math.Sqrt(da * da + dz * dz);
		}

		private (double A, double Z) BlockCentre(ScatterTileKey key, int block)
		{
			double blockA = _ctx.TileCells * _ctx.AroundCellMeters / 4.0, blockZ = _ctx.AcrossTileMeters / 4.0;
			return (key.Around * (double)_ctx.TileCells * _ctx.AroundCellMeters + ((block & 3) + .5) * blockA, key.Across * (double)_ctx.AcrossTileMeters + ((block >> 2) + .5) * blockZ);
		}

		/// <summary>The rows (a prefix of the build order) and cobble blocks a tile is due for from the focus. A whole-tile row is due by the tile centre's distance; a block row by its blocks' centres, and only while the focus is under 60 m above the ground.</summary>
		internal (int Rows, int BlockMask) DueFor(ScatterTileKey key, double focusAround, double focusAcross, float heightAboveGroundM)
		{
			IReadOnlyList<RingScatterClass> order = _ctx.Table.BuildOrder;
			double distance = TileDistance(key, focusAround, focusAcross), scale = DistanceScale;
			int rows = 0, blockMask = 0;
			for (int r = 0; r < order.Count; r++)
			{
				RingScatterClass row = order[r];
				if (row.BlockM == 0)
				{
					if (distance <= row.ReachM * scale + DueMarginM) rows = r + 1;
					continue;
				}
				if (heightAboveGroundM >= CobbleMaximumHeightM) continue;
				double radius = row.ReachM * scale + BlockDueMarginM;
				if (distance > radius + TileRadiusM) continue;
				int rowMask = 0;
				for (int b = 0; b < 16; b++)
				{
					(double a, double z) = BlockCentre(key, b);
					double da = WrapDelta(a - focusAround), dz = z - focusAcross;
					if (Math.Sqrt(da * da + dz * dz) <= radius) rowMask |= 1 << b;
				}
				if (rowMask == 0) continue;
				blockMask |= rowMask;
				rows = Math.Max(rows, r + 1);
			}
			return (rows, blockMask);
		}

		/// <summary>The prefix of rows a tile keeps: due radius plus another 300 m of hysteresis (block rows: block radius plus the tile's radius plus 300 m).</summary>
		internal int KeepRows(ScatterTileKey key, double focusAround, double focusAcross)
		{
			IReadOnlyList<RingScatterClass> order = _ctx.Table.BuildOrder;
			double distance = TileDistance(key, focusAround, focusAcross), scale = DistanceScale;
			int keep = 0;
			for (int r = 0; r < order.Count; r++)
			{
				RingScatterClass row = order[r];
				double radius = row.BlockM == 0 ? row.ReachM * scale + DueMarginM + DueMarginM : row.ReachM * scale + BlockDueMarginM + TileRadiusM + DueMarginM;
				if (distance <= radius) keep = r + 1;
			}
			return keep;
		}

		private sealed record JobResult(List<ScatterRecord> Records, int BuiltRows, ushort BlockMask, bool DiskLoaded, bool DiskWritten, bool Failed);

		private sealed class StreamJob
		{
			public required ScatterTileKey Key;
			public required ScatterTileState State;
			public required ulong Version;
			public required CancellationTokenSource Cancellation;
			public required Task<JobResult> Task;
		}

		private bool DiskAllowed => _disk != null && _diskEnabled() && RingScatterTileFile.DiskCacheAllowed(_template.WorldFeatureMask, _ledger, RingScatterTileFile.ScatterDiskCacheForTests);

		private void StartJob(ScatterTileKey key, ScatterTileState state, int targetRows, int dueMask, RingLodSurfaceEditSnapshot edits, ulong version)
		{
			ScatterBuildContext ctx = _ctx;
			var cancellation = new CancellationTokenSource();
			CancellationToken token = cancellation.Token;
			// Snapshots for the worker: the state itself is only touched on the main thread.
			List<ScatterRecord> existing = new(state.Records);
			int oldBuilt = state.BuiltRows;
			ushort oldMask = state.CobbleBlockMask;
			bool tryDisk = !state.DiskTried && DiskAllowed && !(_touchesEdits?.Invoke(key, edits) ?? false);
			bool diskWritable = DiskAllowed && !(_touchesEdits?.Invoke(key, edits) ?? false);
			string path = _disk != null ? RingScatterTileFile.PathFor(_disk.Root, _disk.Identity, key.Around, key.Across) : string.Empty;
			state.DiskTried = true;
			ScatterTreeSet trees = state.Trees;
			RingScatterClassTable table = ctx.Table;
			state.InFlight = true;
			_jobs[key] = new StreamJob
			{
				Key = key, State = state, Version = version, Cancellation = cancellation,
				Task = Task.Run(() => RunJob(ctx, trees, table, key, existing, oldBuilt, oldMask, targetRows, dueMask, tryDisk, diskWritable, path, token))
			};
			if (_jobs.Count > MaxJobsInFlightObserved) MaxJobsInFlightObserved = _jobs.Count;
			JobsStarted++;
		}

		private JobResult RunJob(ScatterBuildContext ctx, ScatterTreeSet trees, RingScatterClassTable table, ScatterTileKey key, List<ScatterRecord> records, int built, ushort mask,
			int targetRows, int dueMask, bool tryDisk, bool diskWritable, string path, CancellationToken token)
		{
			try
			{
				bool diskLoaded = false;
				if (tryDisk && RingScatterTileFile.TryLoad(path, key.Around, key.Across, table, out ScatterTileData? data) && data != null)
				{
					records = new List<ScatterRecord>(data.Records); built = data.BuiltRows; mask = data.CobbleBlockMask; diskLoaded = true;
				}
				IReadOnlyList<RingScatterClass> order = table.BuildOrder;
				int newBuilt = Math.Max(built, targetRows);
				bool anyBlockRow = false;
				for (int r = 0; r < newBuilt; r++) if (order[r].BlockM > 0) anyBlockRow = true;
				ushort newMask = anyBlockRow ? (ushort)(mask | dueMask) : mask;
				bool builtAny = false;
				if (newBuilt > built || newMask != mask)
				{
					using RingTerrainSampler sampler = _template.CreateIndependentCopy();
					for (int r = 0; r < newBuilt; r++)
					{
						if (token.IsCancellationRequested) return new JobResult(records, built, mask, diskLoaded, false, true);
						RingScatterClass row = order[r];
						int blocks = 0;
						if (row.BlockM == 0) { if (r < built) continue; blocks = 0xFFFF; }
						else
						{
							blocks = r >= built ? newMask : newMask & ~mask;
							if (blocks == 0) continue;
						}
						ScatterRowsResult result = BuildScatterRows(ctx, sampler, key, trees, records, r, r + 1, blocks);
						records.AddRange(result.Records);
						builtAny = true;
					}
					var index = new Dictionary<ushort, int>();
					for (int i = 0; i < order.Count; i++) index[order[i].Id] = i;
					records.Sort((a, b) => { int c = index[a.ClassId].CompareTo(index[b.ClassId]); return c != 0 ? c : a.Id.CompareTo(b.Id); });
				}
				bool wrote = false;
				if (diskWritable && builtAny && !token.IsCancellationRequested)
					wrote = RingScatterTileFile.TryWrite(path, _disk!.Root, _disk.BudgetBytes, _disk.MinimumFreeBytes, key.Around, key.Across, table, new ScatterTileData(newBuilt, newMask, records));
				return new JobResult(records, newBuilt, newMask, diskLoaded, wrote, false);
			}
			catch (Exception)
			{
				return new JobResult(new List<ScatterRecord>(), 0, 0, false, false, true);
			}
		}
	}
}
