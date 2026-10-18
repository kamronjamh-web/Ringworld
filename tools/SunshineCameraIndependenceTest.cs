using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>
/// P0 of docs/cloud-camera-independence-plan.md. Sweeps the CollectRenderWeatherSystems centre at the Certified capture
/// pose with the weather clock frozen and asserts that records are camera independent, selection is complete (checked
/// against a brute-force scan of every macro cell), and the P5 continuous cluster term is continuous and equals the
/// CPU term at lattice corners. Reads production state only.
/// </summary>
public partial class SunshineCameraIndependenceTest : RefCounted
{
    const double PoseAround = 8150667.255809009;
    const double PoseAcross = 119474.33742621914;
    const double FrozenTime = 4506.117868;
    const double StepMeters = 100.0;
    const int AroundHalfSteps = 250;   // +-25 km = 50 km along the ring
    const int AcrossHalfSteps = 100;   // +-10 km = 20 km across
    const double MarginMeters = 8000.0;

    readonly List<string> _failures = new();
    RingVolumetricCloudLayer _layer = null!;
    double _circumference;
    Vector2 _wind;
    double _macroCell;
    List<(int Id, double Around, double Across, double Major)> _allNatural = new();

    void Fail(string message)
    {
        if (_failures.Count < 12) _failures.Add(message);
        else if (_failures.Count == 12) _failures.Add("... further failures suppressed");
    }

    public int Run(Node layerNode, double handoffEndMeters, double lightReachMeters)
    {
        try { return RunInternal(layerNode, handoffEndMeters, lightReachMeters); }
        catch (Exception exception)
        {
            GD.PushError("SUNSHINE_CAMERA_INDEPENDENCE=FAIL " + exception);
            return 1;
        }
    }

    int RunInternal(Node layerNode, double handoffEndMeters, double lightReachMeters)
    {
        _layer = (RingVolumetricCloudLayer)layerNode;
        var grid = _layer.GetNode<VoxelChunkGridManager>(_layer.GridManagerPath);
        _circumference = grid.GetCanonicalRingCircumferenceMeters64();
        _wind = _layer.WeatherWindMetersPerSecond;
        _macroCell = Mathf.Clamp(_layer.MacroWeatherCellMeters, 16000.0f, 64000.0f);
        double radius = handoffEndMeters + lightReachMeters + MarginMeters;
        BuildBruteForceList(grid);
        GD.Print($"[camera-independence] radius={radius:0} m (handoffEnd={handoffEndMeters:0} + light={lightReachMeters:0} + margin={MarginMeters:0}); "
            + $"ring_systems_live={_allNatural.Count} circumference={_circumference:0} wind=({_wind.X},{_wind.Y}) lifecycle={_layer.NaturalWeatherLifecyclesEnabled}");

        // (a) + (b) + (d): sweep the collection centre.
        var records = new Dictionary<int, RingVolumetricCloudLayer.RenderWeatherSystem>();
        var buffer = new List<RingVolumetricCloudLayer.RenderWeatherSystem>(96);
        var counts = new List<int>();
        var timer = new Stopwatch();
        int positions = 0;
        for (int k = -AroundHalfSteps; k <= AroundHalfSteps; k++)
            CheckCenter(new RingCanonicalPosition(PoseAround + k * StepMeters, 0.0, PoseAcross), radius, records, buffer, counts, timer, ref positions);
        for (int j = -AcrossHalfSteps; j <= AcrossHalfSteps; j++)
            CheckCenter(new RingCanonicalPosition(PoseAround, 0.0, PoseAcross + j * StepMeters), radius, records, buffer, counts, timer, ref positions);
        // Ring seam and far side: completeness must survive around wrapping.
        foreach (double seamAround in new[] { 1000.0, _circumference - 1000.0, _circumference * 0.5, -2500.0 })
            CheckCenter(new RingCanonicalPosition(seamAround, 0.0, PoseAcross), radius, records, buffer, counts, timer, ref positions);
        double microsPerCall = timer.Elapsed.TotalMilliseconds * 1000.0 / Math.Max(1, positions);
        LogDistribution("selection radius", counts);

        // Smaller radii for the N decision (handoff only, and handoff + light).
        foreach ((string label, double r) in new[] { ("handoffEnd", handoffEndMeters), ("handoffEnd+light", handoffEndMeters + lightReachMeters) })
        {
            var c = new List<int>();
            for (int k = -AroundHalfSteps; k <= AroundHalfSteps; k += 5)
            {
                buffer.Clear();
                _layer.CollectRenderWeatherSystems(new RingCanonicalPosition(PoseAround + k * StepMeters, 0.0, PoseAcross), r, FrozenTime, buffer);
                c.Add(buffer.Count);
            }
            LogDistribution(label + $" r={r:0}", c);
        }

        CheckManual(radius, buffer);
        CheckProfilesConstantOverLifetime(radius, buffer);
        CheckClusterTerm();
        CheckCoverageCalibration(handoffEndMeters, buffer);
        CheckMacroMapInterpolation(buffer);
        PrintPoseCheck(radius, buffer);

        if (_failures.Count > 0)
        {
            foreach (string failure in _failures) GD.PushError("SUNSHINE_CAMERA_INDEPENDENCE detail: " + failure);
            GD.PushError($"SUNSHINE_CAMERA_INDEPENDENCE=FAIL failures={_failures.Count}");
            return 1;
        }
        GD.Print($"SUNSHINE_CAMERA_INDEPENDENCE=PASS positions={positions} unique_systems={records.Count} collect_us_per_call={microsPerCall:0.0}");
        return 0;
    }

    // Every live natural system of the whole ring at the frozen time, via the private TryBuildWeatherSystem.
    void BuildBruteForceList(VoxelChunkGridManager grid)
    {
        Type type = typeof(RingVolumetricCloudLayer);
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo build = type.GetMethod("TryBuildWeatherSystem", Any) ?? throw new InvalidOperationException("TryBuildWeatherSystem missing");
        int seed = grid.TerrainSampler?.Seed ?? 1337;
        double maxRadius = Mathf.Clamp(_layer.MaximumMacroSystemRadiusMeters, 40000.0f, 160000.0f);
        int aroundCount = Math.Max(1, Mathf.CeilToInt((float)(_circumference / _macroCell)));
        int minAcross = Mathf.FloorToInt((float)((-maxRadius - _wind.Y * FrozenTime) / _macroCell)) - 2;
        int maxAcross = Mathf.CeilToInt((float)((grid.RingAcrossWidthMeters + maxRadius - _wind.Y * FrozenTime) / _macroCell)) + 2;
        PropertyInfo? idP = null, aroundP = null, acrossP = null, majorP = null;
        for (int z = minAcross; z <= maxAcross; z++)
        for (int a = 0; a < aroundCount; a++)
        {
            object?[] args = { a, z, seed, FrozenTime, null };
            if (!(bool)build.Invoke(_layer, args)!) continue;
            object def = args[4]!;
            Type defType = def.GetType();
            idP ??= defType.GetProperty("SystemId"); aroundP ??= defType.GetProperty("SourceAroundMeters");
            acrossP ??= defType.GetProperty("SourceAcrossMeters"); majorP ??= defType.GetProperty("MajorRadiusMeters");
            _allNatural.Add(((int)idP!.GetValue(def)!, (double)aroundP!.GetValue(def)!, (double)acrossP!.GetValue(def)!, (float)majorP!.GetValue(def)!));
        }
    }

    double ShortestDelta(double from, double to)
    {
        double delta = Wrap(to) - Wrap(from);
        if (delta > _circumference * 0.5) delta -= _circumference;
        if (delta < -_circumference * 0.5) delta += _circumference;
        return delta;
    }

    double Wrap(double value)
    {
        double wrapped = value % _circumference;
        return wrapped < 0.0 ? wrapped + _circumference : wrapped;
    }

    HashSet<int> ExpectedNaturalIds(RingCanonicalPosition center, double radius)
    {
        double sourceAround = Wrap(center.AroundMeters - _wind.X * FrozenTime);
        double sourceAcross = center.AcrossMeters - _wind.Y * FrozenTime;
        var ids = new HashSet<int>();
        foreach ((int id, double around, double across, double major) in _allNatural)
        {
            double da = ShortestDelta(sourceAround, around), dz = across - sourceAcross;
            if (Math.Sqrt(da * da + dz * dz) - major < radius) ids.Add(id);
        }
        return ids;
    }

    void CheckCenter(RingCanonicalPosition center, double radius, Dictionary<int, RingVolumetricCloudLayer.RenderWeatherSystem> records,
        List<RingVolumetricCloudLayer.RenderWeatherSystem> buffer, List<int> counts, Stopwatch timer, ref int positions)
    {
        buffer.Clear();
        timer.Start();
        _layer.CollectRenderWeatherSystems(center, radius, FrozenTime, buffer);
        timer.Stop();
        positions++;
        counts.Add(buffer.Count);
        var seen = new HashSet<int>();
        foreach (RingVolumetricCloudLayer.RenderWeatherSystem record in buffer)
        {
            if (!seen.Add(record.SystemId)) Fail($"duplicate system {record.SystemId} at around={center.AroundMeters:0.0}");
            if (records.TryGetValue(record.SystemId, out RingVolumetricCloudLayer.RenderWeatherSystem first))
            {
                if (!first.Equals(record)) Fail($"record for system {record.SystemId} differs between centres (around={center.AroundMeters:0.0}, across={center.AcrossMeters:0.0})");
            }
            else records[record.SystemId] = record;
        }
        HashSet<int> expected = ExpectedNaturalIds(center, radius);
        foreach (int id in expected) if (!seen.Contains(id)) Fail($"selection incomplete: system {id} missing at around={center.AroundMeters:0.0}, across={center.AcrossMeters:0.0}");
        foreach (int id in seen) if (!expected.Contains(id)) Fail($"selection returned system {id} outside radius at around={center.AroundMeters:0.0}");
    }

    void LogDistribution(string label, List<int> counts)
    {
        var sorted = counts.OrderBy(c => c).ToList();
        int Pct(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
        var histogram = sorted.GroupBy(c => c).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}");
        GD.Print($"[camera-independence] system counts ({label}) n={sorted.Count} min={sorted[0]} median={Pct(0.5)} p95={Pct(0.95)} p99={Pct(0.99)} max={sorted[^1]} "
            + $">32={sorted.Count(c => c > 32)} >48={sorted.Count(c => c > 48)} >64={sorted.Count(c => c > 64)} histogram=[{string.Join(" ", histogram)}]");
    }

    void CheckManual(double radius, List<RingVolumetricCloudLayer.RenderWeatherSystem> buffer)
    {
        var pose = new RingCanonicalPosition(PoseAround, 0.0, PoseAcross);
        buffer.Clear();
        _layer.CollectRenderWeatherSystems(pose, radius, FrozenTime, buffer);
        var baseline = buffer.Select(s => s.SystemId).OrderBy(i => i).ToList();

        const float manualRadius = 40000.0f;
        // Inside support (1.3 x radius) but outside the bare radius: proves the 1.3x support is applied.
        double nearAround = PoseAround + radius + manualRadius * 1.15;
        _layer.RegisterManualWeatherAt("camera_independence_near", nearAround, PoseAcross, manualRadius, -9001);
        _layer.RegisterManualWeatherAt("camera_independence_far", PoseAround + radius + manualRadius * 1.5, PoseAcross, manualRadius, -1003);
        buffer.Clear();
        _layer.CollectRenderWeatherSystems(pose, radius, FrozenTime, buffer);
        var near = buffer.FirstOrDefault(s => s.SystemId == -9001);
        if (near.SystemId != -9001) Fail("manual system -9001 inside 1.3x support was not collected");
        else
        {
            const uint expectedFlags = RingVolumetricCloudLayer.RenderWeatherFlagStatic | RingVolumetricCloudLayer.RenderWeatherFlagSculpt | RingVolumetricCloudLayer.RenderWeatherFlagManualFalloff;
            if (near.Flags != expectedFlags || near.SupportMultiplier != 1.3f) Fail($"manual flags/support wrong: flags={near.Flags} support={near.SupportMultiplier}");
            if (Math.Abs(near.CenterAroundMeters - nearAround) > 1.0 || near.MajorRadiusMeters != manualRadius) Fail("manual centre/radius not carried through");
        }
        if (buffer.Any(s => s.SystemId == -1003)) Fail("manual system -1003 beyond 1.3x support was collected");
        if (!buffer.Where(s => s.SystemId > 0).Select(s => s.SystemId).OrderBy(i => i).SequenceEqual(baseline.Where(i => i > 0))) Fail("registering manual weather changed the natural selection");
        _layer.UnregisterManualWeather("camera_independence_near");
        _layer.UnregisterManualWeather("camera_independence_far");
        buffer.Clear();
        _layer.CollectRenderWeatherSystems(pose, radius, FrozenTime, buffer);
        if (buffer.Any(s => s.SystemId < 0)) Fail("manual systems remained after unregister");
    }

    // The profile / sculpt / storm band are per-system properties: they must not change while the system lives.
    void CheckProfilesConstantOverLifetime(double radius, List<RingVolumetricCloudLayer.RenderWeatherSystem> buffer)
    {
        var pose = new RingCanonicalPosition(PoseAround, 0.0, PoseAcross);
        var first = new Dictionary<int, RingVolumetricCloudLayer.RenderWeatherSystem>();
        int persisted = 0, sculpting = 0;
        foreach (double offset in new[] { 0.0, 150.0, 400.0, 900.0, 1500.0, 2200.0 })
        {
            buffer.Clear();
            _layer.CollectRenderWeatherSystems(pose, radius, FrozenTime + offset, buffer);
            foreach (RingVolumetricCloudLayer.RenderWeatherSystem s in buffer)
            {
                if (!first.TryGetValue(s.SystemId, out var f)) { first[s.SystemId] = s; if (s.Sculpts) sculpting++; continue; }
                persisted++;
                if (f.PhysicalProfile != s.PhysicalProfile || f.Flags != s.Flags || f.StormFloorMeters != s.StormFloorMeters || f.StormCeilingMeters != s.StormCeilingMeters)
                    Fail($"system {s.SystemId} changed profile/flags/storm band over its lifetime (t+{offset})");
            }
        }
        GD.Print($"[camera-independence] lifetime check: unique={first.Count} re-observations={persisted} sculpting_systems={sculpting}");
        if (persisted == 0) Fail("lifetime check observed no persistent system (vacuous)");
    }

    // C# mirror of ring_ws_macro_point (RingWeatherSystems.glslinc) for natural systems at the frozen time with overflow fade 1:
    // natural systems by product + strongest, coverage = clamp(base + continuous cluster + macroBias + densityBias, 0.015, 0.985),
    // storm = clamp(0.65 influence + 0.55 precipitation). Returns the UNCLAMPED coverage sum so a base offset can be solved for.
    (float RawCoverage, float Coverage, float Storm, float Precipitation) EvaluateMacro(
        double around, double across, List<RingVolumetricCloudLayer.RenderWeatherSystem> systems, float baseCoverage)
    {
        double sourceAround = Wrap(around - _wind.X * FrozenTime);
        double sourceAcross = across - _wind.Y * FrozenTime;
        float combined = 0.0f, strongest = 0.0f, strongestDensity = 0.0f, strongestPrecipitation = 0.0f;
        foreach (RingVolumetricCloudLayer.RenderWeatherSystem system in systems)
        {
            if (system.IsStatic) continue;
            double da = ShortestDelta(sourceAround, system.CenterAroundMeters), dz = system.CenterAcrossMeters - sourceAcross;
            double major = system.MajorRadiusMeters;
            if (da * da + dz * dz >= major * major) continue;
            double along = system.OrientationCosine * da + system.OrientationSine * dz;
            double cross = -system.OrientationSine * da + system.OrientationCosine * dz;
            double minor = system.MinorRadiusMeters;
            double normalizedSquared = along * along / Math.Max(1.0, major * major) + cross * cross / Math.Max(1.0, minor * minor);
            if (normalizedSquared >= 1.0) continue;
            float t = Mathf.Clamp(((float)Math.Sqrt(normalizedSquared) - 0.32f) / 0.68f, 0.0f, 1.0f);
            float weighted = (1.0f - t * t * (3.0f - 2.0f * t)) * system.Influence;
            combined = 1.0f - (1.0f - combined) * (1.0f - weighted);
            if (weighted <= strongest) continue;
            strongest = weighted; strongestDensity = system.Influence; strongestPrecipitation = system.Precipitation;
        }
        combined = Mathf.Clamp(combined, 0.0f, 1.0f);
        strongest = Mathf.Clamp(strongest, 0.0f, 1.0f);
        float raw = baseCoverage + _layer.SampleContinuousClusterTerm(around, across, FrozenTime)
            + (combined - 0.28f) * 0.46f + strongest * (strongestDensity - 0.5f) * 0.24f;
        float coverage = Mathf.Clamp(raw, 0.015f, 0.985f);
        float x = Mathf.Clamp((coverage - 0.35f) / 0.55f, 0.0f, 1.0f);
        float precipitation = Mathf.Clamp(strongestPrecipitation * combined * (x * x * (3.0f - 2.0f * x)), 0.0f, 1.0f);
        float storm = Mathf.Clamp(combined * 0.65f + precipitation * 0.55f, 0.0f, 1.0f);
        return (raw, coverage, storm, precipitation);
    }

    /// <summary>
    /// Calibration of SunshineBaseCoverageOffset. Legacy: the bridge published ONE camera-local coverage
    /// (QueryLocalWeather(camera).CloudCoverage -> cloud_weather_frame.w) for the whole sky. New: the shader evaluates coverage(x) per
    /// position. Over the sweep this compares the mean legacy value with the mean new value over the visible disc (uniform area weight,
    /// and a 1/r weight that approximates screen coverage), then solves the offset that makes the clamped new mean equal the legacy
    /// mean. Prints the numbers; the default of SunshineBaseCoverageOffset is set from the area-weighted result.
    /// </summary>
    void CheckCoverageCalibration(double discRadiusMeters, List<RingVolumetricCloudLayer.RenderWeatherSystem> buffer)
    {
        const double GridStep = 4000.0;
        double ringWidth = _layer.GetNode<VoxelChunkGridManager>(_layer.GridManagerPath).RingAcrossWidthMeters;
        float baseCoverage = _layer.Coverage;
        var rawArea = new List<float>(); var rawInverse = new List<float>(); var weightInverse = new List<double>();
        double legacyCoverage = 0.0, legacyVisible = 0.0, newVisibleArea = 0.0;
        int cameras = 0;
        var cameraCenters = new List<RingCanonicalPosition>();
        for (int k = -AroundHalfSteps; k <= AroundHalfSteps; k += 25)
            cameraCenters.Add(new RingCanonicalPosition(PoseAround + k * StepMeters, 0.0, PoseAcross));
        for (int j = -AcrossHalfSteps; j <= AcrossHalfSteps; j += 25)
            cameraCenters.Add(new RingCanonicalPosition(PoseAround, 0.0, PoseAcross + j * StepMeters));
        foreach (RingCanonicalPosition camera in cameraCenters)
        {
            RingVolumetricCloudLayer.LocalWeatherState legacy = _layer.QueryLocalWeather(camera);
            float legacyStorm = Mathf.Clamp(legacy.SystemInfluence * 0.65f + legacy.PrecipitationPotential * 0.55f, 0.0f, 1.0f);
            legacyCoverage += legacy.CloudCoverage;
            legacyVisible += Math.Max(legacy.CloudCoverage, legacyStorm);
            buffer.Clear();
            _layer.CollectRenderWeatherSystems(camera, discRadiusMeters + 2.0 * GridStep, FrozenTime, buffer);
            int steps = (int)Math.Ceiling(discRadiusMeters / GridStep);
            for (int ia = -steps; ia <= steps; ia++)
            for (int iz = -steps; iz <= steps; iz++)
            {
                double da = ia * GridStep, dz = iz * GridStep, r = Math.Sqrt(da * da + dz * dz);
                double across = camera.AcrossMeters + dz;
                if (r > discRadiusMeters || across < 0.0 || across > ringWidth) continue;
                var macro = EvaluateMacro(camera.AroundMeters + da, across, buffer, baseCoverage);
                rawArea.Add(macro.RawCoverage);
                newVisibleArea += Math.Max(macro.Coverage, macro.Storm);
                rawInverse.Add(macro.RawCoverage);
                weightInverse.Add(1.0 / Math.Max(r, 2000.0));
            }
            cameras++;
        }
        legacyCoverage /= cameras; legacyVisible /= cameras; newVisibleArea /= Math.Max(1, rawArea.Count);
        double newAreaMean = rawArea.Average(v => Math.Clamp((double)v, 0.015, 0.985));
        double weightSum = weightInverse.Sum();
        double newInverseMean = 0.0;
        for (int i = 0; i < rawInverse.Count; i++) newInverseMean += Math.Clamp((double)rawInverse[i], 0.015, 0.985) * weightInverse[i];
        newInverseMean /= weightSum;

        // Offset o with mean(clamp(raw + o)) == legacy mean, by bisection (the mean is monotonic in o).
        double Solve(Func<double, double> mean)
        {
            double lo = -0.5, hi = 0.5;
            for (int i = 0; i < 40; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (mean(mid) < legacyCoverage) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }
        double areaOffset = Solve(o => rawArea.Average(v => Math.Clamp((double)v + o, 0.015, 0.985)));
        double inverseOffset = Solve(o =>
        {
            double sum = 0.0;
            for (int i = 0; i < rawInverse.Count; i++) sum += Math.Clamp((double)rawInverse[i] + o, 0.015, 0.985) * weightInverse[i];
            return sum / weightSum;
        });
        GD.Print($"[camera-independence] coverage calibration: cameras={cameras} disc_points={rawArea.Count} base={baseCoverage:0.###} "
            + $"legacy mean cloud_weather_frame.w={legacyCoverage:0.####} legacy mean visible={legacyVisible:0.####} | new mean coverage (offset 0): "
            + $"area={newAreaMean:0.####} inverse-distance={newInverseMean:0.####} new mean visible(area)={newVisibleArea:0.####} | "
            + $"proposed SunshineBaseCoverageOffset: area={areaOffset:0.###} inverse-distance={inverseOffset:0.###}");
    }

    /// <summary>
    /// Bilinear error of the macro weather map. The map stores ring_ws_macro_point at texel centres of a MapTexelMeters grid and the GPU
    /// filters bilinearly, so this builds the exact values on such a grid around the camera and around the smallest in-range system
    /// (highest curvature), interpolates at random points, and compares with the exact function. The analytic bound per axis is
    /// h^2/8 * max|f''|; the printed numbers are the measured maxima for coverage, storm level and precipitation.
    /// </summary>
    void CheckMacroMapInterpolation(List<RingVolumetricCloudLayer.RenderWeatherSystem> buffer)
    {
        const double MapTexelMeters = 1000.0; // MacroMapResolution default
        const int Cells = 64;
        float baseCoverage = _layer.Coverage;
        var pose = new RingCanonicalPosition(PoseAround, 0.0, PoseAcross);
        buffer.Clear();
        _layer.CollectRenderWeatherSystems(pose, 200000.0, FrozenTime, buffer);
        if (buffer.Count == 0) { Fail("map interpolation check found no systems"); return; }
        var windows = new List<(double Around, double Across, string Label)> { (PoseAround, PoseAcross, "camera") };
        RingVolumetricCloudLayer.RenderWeatherSystem smallest = buffer.Where(s => !s.IsStatic).OrderBy(s => s.MinorRadiusMeters).First();
        windows.Add((Wrap(smallest.CenterAroundMeters + _wind.X * FrozenTime), smallest.CenterAcrossMeters + _wind.Y * FrozenTime, $"smallest system (minor={smallest.MinorRadiusMeters:0} m)"));
        var random = new Random(1234);
        foreach ((double centerAround, double centerAcross, string label) in windows)
        {
            double originAround = centerAround - Cells * 0.5 * MapTexelMeters, originAcross = centerAcross - Cells * 0.5 * MapTexelMeters;
            var grid = new (float Cov, float Storm, float Precip)[Cells + 1, Cells + 1];
            for (int i = 0; i <= Cells; i++)
            for (int j = 0; j <= Cells; j++)
            {
                var m = EvaluateMacro(originAround + i * MapTexelMeters, originAcross + j * MapTexelMeters, buffer, baseCoverage);
                grid[i, j] = (m.Coverage, m.Storm, m.Precipitation);
            }
            double worstCov = 0.0, worstStorm = 0.0, worstPrecip = 0.0;
            for (int sample = 0; sample < 4000; sample++)
            {
                double u = 1.0 + random.NextDouble() * (Cells - 2), v = 1.0 + random.NextDouble() * (Cells - 2);
                int i0 = (int)u, j0 = (int)v;
                float fu = (float)(u - i0), fv = (float)(v - j0);
                (float Cov, float Storm, float Precip) Lerp(int di, int dj) => grid[i0 + di, j0 + dj];
                float Bi(Func<(float Cov, float Storm, float Precip), float> pick)
                {
                    float a = Mathf.Lerp(pick(Lerp(0, 0)), pick(Lerp(1, 0)), fu);
                    float b = Mathf.Lerp(pick(Lerp(0, 1)), pick(Lerp(1, 1)), fu);
                    return Mathf.Lerp(a, b, fv);
                }
                var exact = EvaluateMacro(originAround + u * MapTexelMeters, originAcross + v * MapTexelMeters, buffer, baseCoverage);
                worstCov = Math.Max(worstCov, Math.Abs(Bi(g => g.Cov) - exact.Coverage));
                worstStorm = Math.Max(worstStorm, Math.Abs(Bi(g => g.Storm) - exact.Storm));
                worstPrecip = Math.Max(worstPrecip, Math.Abs(Bi(g => g.Precip) - exact.Precipitation));
            }
            GD.Print($"[camera-independence] map interpolation ({label}, texel {MapTexelMeters:0} m): max |bilinear - exact| coverage={worstCov:0.#####} storm={worstStorm:0.#####} precipitation={worstPrecip:0.#####}");
            if (worstCov > 0.05 || worstStorm > 0.08) Fail($"macro map bilinear error too large at {label}: coverage {worstCov:0.####} storm {worstStorm:0.####}");
        }
    }

    /// <summary>
    /// Diagnostic only (no assertions): the camera-local values the legacy bridge published sky-wide at the Certified pose, next to the
    /// per-position macro at the same point and the sculpt systems the camera-independent upload would carry.
    /// </summary>
    void PrintPoseCheck(double selectionRadius, List<RingVolumetricCloudLayer.RenderWeatherSystem> buffer)
    {
        var camera = new RingCanonicalPosition(PoseAround, 0.0, PoseAcross);
        RingVolumetricCloudLayer.LocalWeatherState legacy = _layer.QueryLocalWeather(camera);
        float legacyStorm = Mathf.Clamp(legacy.SystemInfluence * 0.65f + legacy.PrecipitationPotential * 0.55f, 0.0f, 1.0f);
        buffer.Clear();
        _layer.CollectRenderWeatherSystems(camera, selectionRadius, FrozenTime, buffer);
        var macro = EvaluateMacro(PoseAround, PoseAcross, buffer, _layer.Coverage);
        GD.Print($"[camera-independence] pose check: legacy camera-local: coverage={legacy.CloudCoverage:0.####} stormFactor={legacyStorm:0.####} "
            + $"systemId={legacy.SystemId} kind={legacy.SystemKind} influence={legacy.SystemInfluence:0.####} precip={legacy.PrecipitationPotential:0.####} "
            + $"dominantProfile={legacy.DominantFormationProfile} cloudBase={legacy.CloudBaseAltitudeMeters:0.#} cloudTop={legacy.CloudTopAltitudeMeters:0.#} "
            + $"legacySculptNonZero={legacy.SystemInfluence > 0.0001f && legacy.SystemId != 0}");
        GD.Print($"[camera-independence] pose check: per-position macro at camera: coverage={macro.Coverage:0.####} (raw {macro.RawCoverage:0.####}) "
            + $"storm={macro.Storm:0.####} precip={macro.Precipitation:0.####} uploadable_systems={buffer.Count}");
        double sourceAround = Wrap(PoseAround - _wind.X * FrozenTime), sourceAcross = PoseAcross - _wind.Y * FrozenTime;
        foreach (RingVolumetricCloudLayer.RenderWeatherSystem s in buffer.Where(s => s.Sculpts).OrderBy(s => s.SystemId))
        {
            double da = ShortestDelta(s.IsStatic ? PoseAround : sourceAround, s.CenterAroundMeters);
            double dz = s.CenterAcrossMeters - (s.IsStatic ? PoseAcross : sourceAcross);
            GD.Print($"[camera-independence] pose check: sculpt system id={s.SystemId} kind={s.Kind} profile={s.PhysicalProfile} flags={s.Flags} "
                + $"dist={Math.Sqrt(da * da + dz * dz):0} m radii=({s.MajorRadiusMeters:0},{s.MinorRadiusMeters:0}) "
                + $"stormBand=({s.StormFloorMeters:0},{s.StormCeilingMeters:0}) influence={s.Influence:0.###} precip={s.Precipitation:0.###}");
        }
    }

    void CheckClusterTerm()
    {
        float cell = Mathf.Max(1000.0f, _layer.FormationCellMeters);
        double step = StepMeters;
        float strength = _layer.WeatherClusteringStrength;
        // Lipschitz bound of the smoothstep-bilinear noise: |dN/dc| <= 1.5 / scale per cell (values are in [0,1]).
        double perCell = strength * 1.5 * (0.62 / Math.Max(2, _layer.SmallWeatherClusterCells) + 0.38 / Math.Max(4, _layer.LargeWeatherClusterCells));
        double bound = perCell * step / cell * 1.02 + 1e-6;
        double maxContinuous = 0.0, maxQuantized = 0.0;
        for (int axis = 0; axis < 2; axis++)
        {
            int half = axis == 0 ? AroundHalfSteps : AcrossHalfSteps;
            float previous = 0.0f, previousQuantized = 0.0f;
            for (int k = -half; k <= half; k++)
            {
                double around = PoseAround + (axis == 0 ? k * step : 0.0);
                double across = PoseAcross + (axis == 1 ? k * step : 0.0);
                float value = _layer.SampleContinuousClusterTerm(around, across, FrozenTime);
                float quantized = _layer.SampleQuantizedClusterTerm(around, across, FrozenTime);
                if (k > -half)
                {
                    maxContinuous = Math.Max(maxContinuous, Math.Abs(value - previous));
                    maxQuantized = Math.Max(maxQuantized, Math.Abs(quantized - previousQuantized));
                }
                previous = value;
                previousQuantized = quantized;
            }
        }
        if (maxContinuous > bound) Fail($"cluster term not continuous: max delta per {step} m = {maxContinuous:0.#####} > bound {bound:0.#####}");
        GD.Print($"[camera-independence] cluster term: continuous max delta/{step:0} m = {maxContinuous:0.#####} (bound {bound:0.#####}); CPU quantized max step = {maxQuantized:0.#####}");

        // Lattice corners: continuous == the CPU value (Quantized replicates the QueryRawWeatherAtTime lines).
        double worst = 0.0;
        int corners = 0;
        int baseCellA = (int)(PoseAround / cell), baseCellZ = (int)(PoseAcross / cell);
        for (int da = -12; da <= 12; da++)
        for (int dz = -12; dz <= 12; dz++)
        {
            double sourceAround = (baseCellA + da) * (double)cell;
            double sourceAcross = (baseCellZ + dz) * (double)cell;
            double around = sourceAround + _wind.X * FrozenTime;
            double across = sourceAcross + _wind.Y * FrozenTime;
            float continuous = _layer.SampleContinuousClusterTerm(around, across, FrozenTime);
            // Probe the CPU cell from just inside it so floor() cannot land one cell below the corner.
            float cpu = _layer.SampleQuantizedClusterTerm(around + 0.25, across + 0.25, FrozenTime);
            worst = Math.Max(worst, Math.Abs(continuous - cpu));
            corners++;
        }
        if (worst > 1e-5) Fail($"cluster term differs from CPU at lattice corners: max |delta| = {worst:0.#######}");
        GD.Print($"[camera-independence] cluster term corners checked={corners} max |continuous - cpu| = {worst:0.#######}");
    }
}
