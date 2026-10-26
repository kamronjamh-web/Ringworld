using System;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>GDScript-callable helpers for tools/wet_world_gpu_test.gd and tools/wet_world_capture.gd: finds a natural MajorFront timeline and compares the GPU wet map with the CPU model.</summary>
[GlobalClass]
public partial class WetWorldCaptureHelper : RefCounted
{
    /// <summary>
    /// Finds a mature natural MajorFront with the observer just inside its rain at scanTime and walks the lifecycle: {ok, around, across, first_gust, first_rain, last_rain, clearing_peak, system_id}.
    /// Times are cloud-clock seconds. (The same scan as StormArrivalCaptureHelper.FindStorm, kept here so the wet world captures do not depend on another track's tool files.)
    /// </summary>
    public Godot.Collections.Dictionary FindStorm(Node layerNode, double scanTime, double aroundStart)
    {
        var layer = (RingVolumetricCloudLayer)layerNode;
        StormArrivalTuning tuning = RingStormArrivalSettings.Current.ToTuning(RingVolumetricCloudLayer.StormArrivalPrecipitationStart, layer.Coverage);
        RingCanonicalPosition? found = null; int id = 0;
        for (double around = aroundStart; around < aroundStart + 6_000_000 && found == null; around += 24000)
        for (double across = 20000; across < 300000 && found == null; across += 16000)
        {
            var p = new RingCanonicalPosition(around, 0.0, across);
            foreach (StormSystemGeometry g in layer.GetStormGeometryAtTime(p, scanTime))
            {
                if (g.KindCode != 3 || g.Envelope < 0.99f) continue;
                float n = StormArrivalMath.NormalizedDistance(g, out _, out _);
                if (n < 0.38f || n > 0.55f || StormArrivalMath.RainProxyAtObserver(g, tuning) < 0.2f) continue;
                found = p; id = g.SystemId; break;
            }
        }
        var result = new Godot.Collections.Dictionary { ["ok"] = found != null };
        if (found == null) return result;
        RingCanonicalPosition o = found.Value;
        double firstGust = double.NaN, firstRain = double.NaN, lastRain = double.NaN, bestClearing = 0.0, clearingTime = double.NaN;
        for (double t = Math.Max(0.0, scanTime - 1500.0); t <= scanTime + 1500.0; t += 2.0)
        {
            StormArrivalState s = layer.QueryStormArrivalAtTime(o, t);
            double precip = layer.GetNaturalWeatherDiagnosticAtTime(o.AroundMeters, o.AcrossMeters, t)["precipitation"].AsDouble();
            if (precip >= RingVolumetricCloudLayer.StormArrivalPrecipitationStart) { if (double.IsNaN(firstRain)) firstRain = t; lastRain = t; }
            if (double.IsNaN(firstGust) && s.GustFront > 0.5f && s.SystemId == id) firstGust = t;
            if (!double.IsNaN(lastRain) && t > lastRain && s.Clearing > bestClearing) { bestClearing = s.Clearing; clearingTime = t; }
        }
        result["around"] = o.AroundMeters; result["across"] = o.AcrossMeters; result["system_id"] = id;
        result["first_gust"] = firstGust; result["first_rain"] = firstRain; result["last_rain"] = lastRain; result["clearing_peak"] = clearingTime;
        return result;
    }

    private ulong _commandId = 1000;

    /// <summary>
    /// Builds a concrete overhang with the player construction overlay: a (2 half + 1)^2 slab whose underside is <paramref name="clearance"/> blocks above the block at
    /// (around, vertical, across), on four corner pillars. Returns the number of blocks placed. Used by the wet world captures for the shelter line.
    /// </summary>
    public int PlaceOverhang(Node overlayNode, long around, int vertical, long across, int half, int clearance)
    {
        var overlay = (SmoothConstructionOverlay)overlayNode;
        int placed = 0;
        void Put(long a, int v, long z)
        {
            var block = new SmoothConstructionBlock(a, v, (int)z);
            overlay.Apply(new SmoothConstructionCommand(1, ++_commandId, _commandId, true, block, SmoothConstructionMaterial.Concrete));
            placed++;
        }
        for (long da = -half; da <= half; da++)
        for (long dz = -half; dz <= half; dz++) Put(around + da, vertical + clearance, across + dz);
        foreach ((long da, long dz) in new[] { (-half, -half), (-half, half), (half, -half), (half, half) })
            for (int v = 0; v < clearance; v++) Put(around + da, vertical + v, across + dz);
        return placed;
    }

    /// <summary>
    /// Compares the read-back wet map (RGBA floats, 256 x 256) with the CPU model at <paramref name="count"/> pseudo-random texels inside the central 200 x 200: the exact nine-lag history at
    /// the texel (layer.QueryRainHistoryAtTime) through WetnessModel with rain now = 0 (the compare runs with the storm map off) and the pinned sun. Returns {worst, count, wet_points, mean_abs}.
    /// </summary>
    public Godot.Collections.Dictionary CompareMapToModel(Node wetNode, Node layerNode, float[] map, int count, float sun)
    {
        var wet = (RingWetWorld)wetNode;
        var layer = (RingVolumetricCloudLayer)layerNode;
        var result = new Godot.Collections.Dictionary { ["worst"] = 1.0, ["count"] = 0, ["wet_points"] = 0, ["mean_abs"] = 1.0 };
        if (map == null || map.Length < WetMapFrame.Size * WetMapFrame.Size * 4 || !wet.HasPublishedFrame) return result;
        WetMapFrame frame = wet.PublishedFrame;
        var rng = new RandomNumberGenerator { Seed = 5150 };
        double time = layer.CloudSimulationTimeSeconds;
        double worst = 0.0, sum = 0.0; int wetPoints = 0;
        string worstText = "";
        var snapshot = new RainHistorySnapshot();
        for (int i = 0; i < count; i++)
        {
            int x = 28 + rng.RandiRange(0, 199), y = 28 + rng.RandiRange(0, 199);
            (double around, double across) = frame.TexelCentre(x, y);
            var position = new RingCanonicalPosition(around, 0.0, across);
            RainHistorySample history = layer.QueryRainHistoryAtTime(position, time);
            layer.BuildRainHistorySnapshot(position, time, snapshot);
            StormArrivalTuning tuning = RingStormArrivalSettings.Current.ToTuning(RingVolumetricCloudLayer.StormArrivalPrecipitationStart, layer.Coverage);
            float prior = WetnessModel.PriorRain(WetnessModel.LagProxies(snapshot, Vector2.Zero, tuning), history.TimeSinceRainSeconds, tuning);
            WetnessState cpu = WetnessModel.Evaluate(new WetnessInputs(history.RecentRainAmount, history.TimeSinceRainSeconds, 0.0f, sun, wet.WindMetersPerSecond, RingWetWorldSettings.Current.DryingSpeed, false, prior));
            int index = (y * WetMapFrame.Size + x) * 4;
            double dFilm = Math.Abs(map[index] - cpu.Film), dSoak = Math.Abs(map[index + 1] - cpu.Soak), dStanding = Math.Abs(map[index + 2] - cpu.Standing);
            double d = Math.Max(dFilm, Math.Max(dSoak, dStanding));
            if (d > worst) { worst = d; worstText = $"texel ({x},{y}) gpu=({map[index]:0.000},{map[index + 1]:0.000},{map[index + 2]:0.000}) cpu=({cpu.Film:0.000},{cpu.Soak:0.000},{cpu.Standing:0.000}) recent={history.RecentRainAmount:0.000} tsr={history.TimeSinceRainSeconds:0.0}"; }
            sum += d;
            if (cpu.Film > 0.05f || cpu.Soak > 0.05f || cpu.Standing > 0.05f || map[index] > 0.05f) wetPoints++;
        }
        result["worst"] = worst; result["count"] = count; result["wet_points"] = wetPoints; result["mean_abs"] = sum / Math.Max(1, count); result["worst_at"] = worstText;
        return result;
    }
}
