using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// TEST ONLY (T5-10, tools/landscape_baseline_common.gd): read-only views of the scatter streamer for the landscape harness. Public so GDScript can call them. Nothing here changes streaming.
/// Per-tile timing is recorded only when ScatterTileTiming is on (tools/ScatterDebugBridge.cs, set before the world boots).
/// </summary>
public partial class RingBiomeDecorationLayer
{
	/// <summary>True when no scatter job runs and no tile is due (also true when the world has no scatter streamer).</summary>
	public bool ScatterIdle => _scatter == null || (_scatter.JobsInFlight == 0 && _scatter.DueRemaining == 0);

	public bool HasScatterStreamer => _scatter != null;

	public void ScatterTileTimingReset() => _scatter?.Timings.Clear();

	/// <summary>JSON array, one object per tile built since the last reset: key, tree ms (-1 = unknown or disk hit), scatter O, B, C worker ms (habitat grid included), jobs.</summary>
	public string ScatterTileTimingJson()
	{
		if (_scatter == null) return "[]";
		var rows = _scatter.Timings.OrderBy(p => p.Key.Around).ThenBy(p => p.Key.Across).Select(p => new
		{
			around = p.Key.Around, across = p.Key.Across, tree_ms = p.Value.TreeMs, tree_disk = p.Value.TreeDiskHit, o_ms = p.Value.OMs, b_ms = p.Value.BMs, c_ms = p.Value.CMs,
			jobs = p.Value.Jobs, scatter_disk_loads = p.Value.ScatterDiskLoads, c_jobs = p.Value.CJobs.Select(j => new[] { j.Ms, j.Blocks })
		});
		return JsonSerializer.Serialize(rows);
	}

	/// <summary>Resident record counts per class id plus tiles and bytes, as JSON.</summary>
	public string ScatterResidentJson()
	{
		var records = new List<ScatterRecord>();
		CopyResidentScatterRecords(records);
		var byClass = records.GroupBy(r => (int)r.ClassId).ToDictionary(g => g.Key.ToString(), g => g.Count());
		return JsonSerializer.Serialize(new { by_class = byClass, tiles_resident = ScatterTilesResident, records = ScatterRecordsResident, memory_bytes = ScatterMemoryBytes, jobs_in_flight = ScatterJobsInFlight, discarded = ScatterResultsDiscarded });
	}

	/// <summary>Resident records of one class within a square window (metres around and across the centre) with the habitat grid's slope and rock exposure at each, as JSON; used to pick tors (outcrops on low exposure) and review spots.</summary>
	public string ScatterRecordsNearJson(double around, double across, double radius, int classId)
	{
		if (_scatter == null || _scatterSampler == null || _scatterEdits == null || _grid == null) return "[]";
		var records = new List<ScatterRecord>();
		CopyResidentScatterRecords(records);
		double circumference = CaptureVegetationLayout().CircumferenceMeters;
		using RingTerrainSampler copy = _scatterSampler.CreateIndependentCopy();
		var grid = new RingHabitatFieldGrid(copy, _scatterEdits, copy.Seed, (long)circumference, (int)_grid.RingAcrossWidthMeters);
		var rows = new List<object>();
		foreach (ScatterRecord r in records)
		{
			if (r.ClassId != classId) continue;
			double a = r.AroundCm * 0.01, z = r.AcrossCm * 0.01;
			double da = a - around; if (da > circumference * .5) da -= circumference; else if (da < -circumference * .5) da += circumference;
			if (System.Math.Abs(da) > radius || System.Math.Abs(z - across) > radius) continue;
			RingHabitatSample h = grid.Sample(a, z);
			rows.Add(new { id = r.Id, size = r.Size, a, z, y = r.SurfaceHeight, exposure = h.RockExposure, slope = h.SlopeDeg, scree = h.Scree, flags = (int)h.Flags });
			if (rows.Count >= 400) break;
		}
		return JsonSerializer.Serialize(rows);
	}
}
