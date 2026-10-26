using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>GDScript-callable scans for tools/storm_arrival_capture.gd: finds a natural MajorFront timeline and a rainbow observer.</summary>
[GlobalClass]
public partial class StormArrivalCaptureHelper : RefCounted
{
    /// <summary>
    /// Finds a mature natural MajorFront with the observer just inside its rain at scanTime and walks the lifecycle: {ok, around, across, first_gust, first_rain, last_rain,
    /// clearing_peak, system_id}. Times are cloud-clock seconds.
    /// </summary>
    public Godot.Collections.Dictionary FindStorm(Node layerNode, double scanTime, double aroundStart, float maxGroundMeters = 1.0e9f)
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
                if (layer.GetGroundHeightForTests(p.AroundMeters, p.AcrossMeters) > maxGroundMeters) continue;
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
        result["around"] = o.AroundMeters; result["across"] = o.AcrossMeters; result["system_id"] = id; result["ground_m"] = layer.GetGroundHeightForTests(o.AroundMeters, o.AcrossMeters);
        result["first_gust"] = firstGust; result["first_rain"] = firstRain; result["last_rain"] = lastRain; result["clearing_peak"] = clearingTime;
        return result;
    }

    /// <summary>
    /// Finds an observer inside a mature natural MajorFront that gets the whole arrival sequence at one fixed point: gust front, fat drops, core and then hail (S3 captures).
    /// Candidates lie on a 2 km grid within 14 km of the storm cell found like <see cref="FindStorm"/>. Returns {ok, around, across, ground_m, first_gust, first_fat, first_core, hail_start, hail_peak,
    /// hail_peak_intensity, hail_end, last_core}; times are cloud-clock seconds.
    /// </summary>
    public Godot.Collections.Dictionary FindArrivalSequence(Node layerNode, double scanTime, double aroundStart, float maxGroundMeters = 1.0e9f)
    {
        var layer = (RingVolumetricCloudLayer)layerNode;
        StormArrivalTuning tuning = RingStormArrivalSettings.Current.ToTuning(RingVolumetricCloudLayer.StormArrivalPrecipitationStart, layer.Coverage);
        var result = new Godot.Collections.Dictionary { ["ok"] = false };
        var centres = new List<RingCanonicalPosition>();
        for (double around = aroundStart; around < aroundStart + 12_000_000 && centres.Count < 14; around += 24000)
        for (double across = 20000; across < 300000 && centres.Count < 14; across += 16000)
        {
            var p = new RingCanonicalPosition(around, 0.0, across);
            bool near = false;
            foreach (RingCanonicalPosition c in centres) if (Math.Abs(c.AroundMeters - p.AroundMeters) < 40000.0 && Math.Abs(c.AcrossMeters - p.AcrossMeters) < 40000.0) { near = true; break; }
            if (near) continue;
            foreach (StormSystemGeometry g in layer.GetStormGeometryAtTime(p, scanTime))
            {
                if (g.KindCode != 3 || g.Envelope < 0.99f) continue;
                if (StormArrivalMath.NormalizedDistance(g, out _, out _) > 0.5f || StormArrivalMath.RainProxyAtObserver(g, tuning) < 0.2f) continue;
                centres.Add(p); break;
            }
        }
        if (centres.Count == 0) return result;
        RingCanonicalPosition? centre = centres[0];
        double bestPeak = 0.0; RingCanonicalPosition best = default; int landCandidates = 0, gustCandidates = 0, fatCandidates = 0; double anyHail = 0.0;
        foreach (RingCanonicalPosition storm in centres)
        {
        for (double da = -14000; da <= 14000; da += 2000)
        for (double dz = -14000; dz <= 14000; dz += 2000)
        {
            var p = new RingCanonicalPosition(storm.AroundMeters + da, 0.0, Math.Clamp(storm.AcrossMeters + dz, 5000.0, 300000.0));
            float groundHere = layer.GetGroundHeightForTests(p.AroundMeters, p.AcrossMeters);
            if (groundHere > maxGroundMeters || groundHere < 3.0f) continue;   // land only
            landCandidates++;
            double peak = 0.0; bool gust = false, fat = false;
            for (double t = Math.Max(0.0, scanTime - 1500.0); t <= scanTime + 1500.0; t += 12.0)
            {
                StormArrivalState s = layer.QueryStormArrivalAtTime(p, t);
                if (s.GustFront > 0.5f) gust = true;
                if (s.FatDrop > 0.3f) fat = true;
                anyHail = Math.Max(anyHail, s.HailIntensity);
                if (gust && fat) peak = Math.Max(peak, s.HailIntensity);
            }
            if (gust) gustCandidates++; if (fat) fatCandidates++;
            if (peak > bestPeak) { bestPeak = peak; best = p; }
        }
        if (bestPeak >= 0.6) break;
        }
        if (bestPeak < 0.3) { result["best_peak"] = bestPeak; result["land_candidates"] = landCandidates; result["gust_candidates"] = gustCandidates; result["fat_candidates"] = fatCandidates; result["any_hail"] = anyHail; result["centre_around"] = centre.Value.AroundMeters; return result; }
        double firstGust = double.NaN, firstFat = double.NaN, firstCore = double.NaN, hailStart = double.NaN, hailPeakTime = double.NaN, hailEnd = double.NaN, lastCore = double.NaN, hailPeak = 0.0;
        for (double t = Math.Max(0.0, scanTime - 1500.0); t <= scanTime + 1500.0; t += 2.0)
        {
            StormArrivalState s = layer.QueryStormArrivalAtTime(best, t);
            if (double.IsNaN(firstGust) && s.GustFront > 0.5f) firstGust = t;
            if (double.IsNaN(firstFat) && s.FatDrop > 0.3f) firstFat = t;
            if (double.IsNaN(firstCore) && s.Core > 0.3f) firstCore = t;
            if (s.Core > 0.3f) lastCore = t;
            if (s.HailIntensity > 0.3f) { if (double.IsNaN(hailStart)) hailStart = t; hailEnd = t; }
            if (s.HailIntensity > hailPeak) { hailPeak = s.HailIntensity; hailPeakTime = t; }
        }
        result["ok"] = true; result["around"] = best.AroundMeters; result["across"] = best.AcrossMeters; result["ground_m"] = layer.GetGroundHeightForTests(best.AroundMeters, best.AcrossMeters);
        result["first_gust"] = firstGust; result["first_fat"] = firstFat; result["first_core"] = firstCore; result["last_core"] = lastCore;
        result["hail_start"] = hailStart; result["hail_peak"] = hailPeakTime; result["hail_peak_intensity"] = hailPeak; result["hail_end"] = hailEnd;
        return result;
    }

    /// <summary>
    /// Terrain relief scan around a point for the valley mist capture: returns the lowest cell, the highest cell, and the mean ground, on a stepMeters grid within radiusMeters.
    /// {valley_around, valley_across, valley_m, ridge_around, ridge_across, ridge_m, mean_m}.
    /// </summary>
    public Godot.Collections.Dictionary ScanRelief(Node layerNode, double aroundCentre, double acrossCentre, double radiusMeters, double stepMeters)
    {
        var layer = (RingVolumetricCloudLayer)layerNode;
        double lo = double.MaxValue, hi = double.MinValue, sum = 0.0; int count = 0;
        double loA = 0, loZ = 0, hiA = 0, hiZ = 0;
        for (double da = -radiusMeters; da <= radiusMeters; da += stepMeters)
        for (double dz = -radiusMeters; dz <= radiusMeters; dz += stepMeters)
        {
            if (da * da + dz * dz > radiusMeters * radiusMeters) continue;
            double a = aroundCentre + da, z = Math.Clamp(acrossCentre + dz, 5000.0, 300000.0);
            double h = layer.GetGroundHeightForTests(a, z);
            sum += h; count++;
            if (h < lo && h > 2.0) { lo = h; loA = a; loZ = z; }   // the valley is the lowest LAND cell (water would put the observer in a lake)
            if (h > hi) { hi = h; hiA = a; hiZ = z; }
        }
        return new Godot.Collections.Dictionary { ["valley_around"] = loA, ["valley_across"] = loZ, ["valley_m"] = lo, ["ridge_around"] = hiA, ["ridge_across"] = hiZ, ["ridge_m"] = hi, ["mean_m"] = count > 0 ? sum / count : 0.0 };
    }

    public Godot.Collections.Array HailDebug(Node layerNode, double around, double across, double t)
    {
        var layer = (RingVolumetricCloudLayer)layerNode;
        var tuning = RingStormArrivalSettings.Current.ToTuning(RingVolumetricCloudLayer.StormArrivalPrecipitationStart, layer.Coverage);
        var list = new Godot.Collections.Array();
        foreach (StormSystemGeometry g in layer.GetStormGeometryAtTime(new RingCanonicalPosition(around, 0.0, across), t))
        {
            float sev = StormArrivalMath.Severity(g);
            float proxy = StormArrivalMath.RainProxyAtObserver(g, tuning);
            float peak = StormArrivalMath.RainProxy(0f, g.Density, g.PrecipitationPotential, tuning.BaseCoverage);
            float hail = StormArrivalMath.HailAt(g, sev, Mathf.Min(1f, proxy / Mathf.Max(peak, 0.1f)), 0f, tuning.Hail, out float size);
            list.Add(new Godot.Collections.Dictionary { ["kind"] = g.KindCode, ["L"] = g.LifecycleCode, ["env"] = g.Envelope, ["n"] = StormArrivalMath.NormalizedDistance(g, out _, out _), ["sev"] = sev, ["proxy"] = proxy, ["peak"] = peak, ["hail"] = hail, ["P"] = g.PrecipitationPotential, ["dens"] = g.Density });
        }
        return list;
    }

    /// <summary>World-space direction of a ring-frame horizontal direction (x around, y across) at a canonical position.</summary>
    public Vector3 RingDirectionToWorld(Node gridNode, double around, double across, Vector2 direction)
    {
        var grid = (VoxelChunkGridManager)gridNode;
        RingLodGeometrySnapshot geometry = grid.CaptureLodGeometrySnapshot();
        Vector3 origin = geometry.CanonicalToWorld(around, 0.0, across);
        Vector3 aroundAxis = (geometry.CanonicalToWorld(around + 10.0, 0.0, across) - origin).Normalized();
        Vector3 acrossAxis = (geometry.CanonicalToWorld(around, 0.0, across + 10.0) - origin).Normalized();
        return (aroundAxis * direction.X + acrossAxis * direction.Y).Normalized();
    }

    /// <summary>Wind speed and direction (around, across) at the observer from the shared wind query, for the gust capture log.</summary>
    public Godot.Collections.Dictionary WindAt(Node layerNode, double around, double across, double time)
    {
        var layer = (RingVolumetricCloudLayer)layerNode;
        RingWindSnapshot w = layer.QueryWindSnapshotAtTime(new RingCanonicalPosition(around, 0.0, across), time);
        return new Godot.Collections.Dictionary { ["mean_speed"] = w.MeanWind.Length(), ["instant_speed"] = w.InstantWind.Length(), ["mean"] = w.MeanWind };
    }

    /// <summary>The sun direction in the ring frame (x around, y up, z across) at a canonical position, from the world direction to the sun.</summary>
    public Vector3 SunInRingFrame(Node gridNode, double around, double across, Vector3 toSunWorld)
    {
        var grid = (VoxelChunkGridManager)gridNode;
        RingLodGeometrySnapshot geometry = grid.CaptureLodGeometrySnapshot();
        Vector3 origin = geometry.CanonicalToWorld(around, 0.0, across);
        Vector3 aroundAxis = (geometry.CanonicalToWorld(around + 10.0, 0.0, across) - origin).Normalized();
        Vector3 acrossAxis = (geometry.CanonicalToWorld(around, 0.0, across + 10.0) - origin).Normalized();
        Vector3 upAxis = (geometry.CanonicalToWorld(around, 10.0, across) - origin).Normalized();
        Vector3 s = toSunWorld.Normalized();
        return new Vector3(s.Dot(aroundAxis), s.Dot(upAxis), s.Dot(acrossAxis));
    }

    /// <summary>
    /// Observer for a rainbow: 2.5 km outside the rain rim of a mature MajorFront on the side opposite the antisolar direction, so the antisolar horizontal direction runs into the rain.
    /// sunRing is the direction to the sun in the ring frame (the day-night cycle keeps it fixed in that frame as the player moves). Returns {ok, around, across, sun_elevation, rain_rim_m, anti_h_around, anti_h_across}.
    /// </summary>
    public Godot.Collections.Dictionary PlaceRainbowObserver(Node layerNode, Vector3 sunRing, double time, double aroundStart)
    {
        var layer = (RingVolumetricCloudLayer)layerNode;
        StormArrivalTuning tuning = RingStormArrivalSettings.Current.ToTuning(RingVolumetricCloudLayer.StormArrivalPrecipitationStart, layer.Coverage);
        var result = new Godot.Collections.Dictionary { ["ok"] = false };
        var h = new Vector2(-sunRing.X, -sunRing.Z);
        if (h.LengthSquared() < 1e-4f) return result;
        h = h.Normalized();
        double circumference = layer.GetRingCircumferenceForTests();
        for (double around = aroundStart; around < aroundStart + 6_000_000; around += 24000)
        for (double across = 20000; across < 300000; across += 16000)
        {
            var p = new RingCanonicalPosition(around, 0.0, across);
            foreach (StormSystemGeometry g in layer.GetStormGeometryAtTime(p, time))
            {
                if (g.KindCode != 3 || g.Envelope < 0.99f) continue;
                if (StormArrivalMath.NormalizedDistance(g, out _, out _) > 0.9f) continue;
                double centreAround = around - g.OffsetMeters.X, centreAcross = across - g.OffsetMeters.Y;
                StormArrivalMath.ResolveRims(g.Density, g.PrecipitationPotential, tuning, out _, out float rainRim, out float presence);
                if (presence < 0.9f || rainRim < 0.2f) continue;
                float cosO = g.OrientationCos, sinO = g.OrientationSin;
                float along = cosO * h.X + sinO * h.Y, cross = -sinO * h.X + cosO * h.Y;
                float ray = 1.0f / Mathf.Sqrt(along * along / (g.MajorRadiusMeters * g.MajorRadiusMeters) + cross * cross / (g.MinorRadiusMeters * g.MinorRadiusMeters));
                float rimMeters = rainRim * ray;
                double gap = 2500.0;
                double oa = centreAround - h.X * (rimMeters + gap), oz = centreAcross - h.Y * (rimMeters + gap);
                if (oz < 15000.0 || oz > 300000.0) continue;
                oa = ((oa % circumference) + circumference) % circumference;
                result["ok"] = true; result["around"] = oa; result["across"] = oz; result["sun_elevation"] = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(sunRing.Y, -1f, 1f)));
                result["rain_rim_m"] = rimMeters; result["anti_h_around"] = h.X; result["anti_h_across"] = h.Y; result["system_id"] = g.SystemId;
                return result;
            }
        }
        return result;
    }

    /// <summary>The published weather mist: {mist, added_sigma, sigma_mist, height_factor, revision, publishes}.</summary>
    public Godot.Collections.Dictionary MistState() => new()
    {
        ["mist"] = RingAtmosphereModel.WeatherMist, ["added_sigma"] = RingAtmosphereModel.WeatherMistAddedSigma, ["sigma_mist"] = RingAtmosphereModel.HazeSigmaMist,
        ["height_factor"] = RingAtmosphereModel.WeatherMistHeightFactor, ["revision"] = (long)RingAtmosphereModel.Revision,
        ["publishes"] = RingAtmosphereModel.WeatherMistPublishCount
    };
}
