using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Megastorm WO5 (docs/megastorm-design.md 3.4): the far layer's analytic trailing / precursor table. Checks that RingCloudLayers.EvaluateMap is bit
/// identical to a verbatim copy of its pre-extraction body (AccumulateSystems refactor), that MegastormFarCoverage.Build composes with the natural map
/// as EvaluateMap does with the T/P records present (two winds, three across positions), inactive behaviour, determinism, range and the T plateau.
/// Run: tools/megastorm_far_coverage_test.gd (headless). Prints MEGASTORM_FAR_COVERAGE=PASS.
/// </summary>
public partial class MegastormFarCoverageTest : RefCounted
{
    const double TestTime = 4000.0;
    const double LeadAround = 5_000_000.0;
    static readonly double[] Across = { 40_000.0, 159_000.0, 280_000.0 };

    public int Run(Node layerNode, bool verbose)
    {
        var layer = (RingVolumetricCloudLayer)layerNode;
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        void Info(string message) => Console.WriteLine("MEGASTORM_FAR_COVERAGE_INFO " + message);
        Vector2 savedWind = layer.WeatherWindMetersPerSecond;
        try
        {
            RunChecks(layer, Check, Info);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.ToString());
            return 1;
        }
        finally
        {
            layer.MegastormSource = null;
            layer.WeatherWindMetersPerSecond = savedWind;
        }
        if (failures.Count != 0)
        {
            Console.Error.WriteLine("MEGASTORM_FAR_COVERAGE=FAIL " + string.Join(" | ", failures.Take(20)));
            return 1;
        }
        Console.WriteLine("MEGASTORM_FAR_COVERAGE=PASS");
        return 0;
    }

    static RingCanonicalPosition PointAtXi(MegastormModel model, double xi, double across, double t)
    {
        double lead = model.LeadOffset(across, t);
        double around = MegastormModel.WrapPositive(model.AroundAt(t) - model.State.Direction * (xi + lead), model.Settings.CircumferenceMeters);
        return new RingCanonicalPosition(around, 0.0, across);
    }

    static MegastormModel NewModel(double leadAround = LeadAround)
    {
        var model = new MegastormModel();
        model.Trigger(new MegastormTrigger("far_coverage_test", leadAround, 1, 0.0, 350.0f, 0.0f, 7));
        return model;
    }

    static CloudLayerPoint MakePoint(RingVolumetricCloudLayer layer, double around, double across, double t, float coverage, float circumference)
    {
        Vector2 wind = layer.WeatherWindMetersPerSecond;
        Vector2 shift = new((float)(-wind.X * t), (float)(-wind.Y * t));
        shift.X -= circumference * MathF.Floor(shift.X / circumference);
        return new CloudLayerPoint((float)around, (float)across, shift, circumference, coverage, 0.5f, 0.0f, 0.0f, MathF.Atan2(wind.Y, wind.X), Vector3.Zero);
    }

    static void ToLayerSystems(List<RingVolumetricCloudLayer.RenderWeatherSystem> source, List<CloudLayerSystem> into, Func<RingVolumetricCloudLayer.RenderWeatherSystem, bool> keep)
    {
        into.Clear();
        foreach (var s in source) if (keep(s)) into.Add(new CloudLayerSystem(s, 1.0f));
    }

    void RunChecks(RingVolumetricCloudLayer layer, Action<bool, string> check, Action<string> info)
    {
        double t = TestTime;
        CloudLayerTable table = CloudLayerTable.Shared;
        MegastormModel model = NewModel();
        double circumference = model.Settings.CircumferenceMeters;
        float circumferenceF = (float)circumference;

        // ---- a. EvaluateMap is bit identical to the verbatim pre-extraction body ----
        {
            layer.MegastormSource = model;
            var rng = new Random(12345);
            var render = new List<RingVolumetricCloudLayer.RenderWeatherSystem>();
            var systems = new List<CloudLayerSystem>();
            int compared = 0, mismatches = 0, withSystems = 0;
            double[] times = { 1000.0, 4000.0, 9000.0 };
            for (int i = 0; i < 200; i++)
            {
                double time = times[i % times.Length];
                // Half the points sit inside the megastorm pass so the front-type rows and weatherness paths run.
                double across = rng.NextDouble() * 318_000.0;
                RingCanonicalPosition p = i % 2 == 0
                    ? PointAtXi(model, -280_000.0 + rng.NextDouble() * 700_000.0, across, time)
                    : new RingCanonicalPosition(rng.NextDouble() * circumference, 0.0, across);
                render.Clear();
                layer.CollectRenderWeatherSystems(p, 200_000.0, time, render);
                ToLayerSystems(render, systems, _ => true);
                if (systems.Count > 0) withSystems++;
                float coverage = (float)rng.NextDouble();
                CloudLayerPoint point = MakePoint(layer, p.AroundMeters, p.AcrossMeters, time, coverage, circumferenceF);
                point = point with { ClusterMid = (float)(rng.NextDouble() - 0.5) * 0.3f, ClusterHigh = (float)(rng.NextDouble() - 0.5) * 0.3f,
                    SunLocal = new Vector3(0.3f, 0.2f, 0.9f).Normalized() };
                Vector4 a = RingCloudLayers.EvaluateMap(point, systems, table);
                Vector4 b = OldEvaluateMap(point, systems, table);
                compared++;
                if (BitConverter.SingleToInt32Bits(a.X) != BitConverter.SingleToInt32Bits(b.X) || BitConverter.SingleToInt32Bits(a.Y) != BitConverter.SingleToInt32Bits(b.Y)
                    || BitConverter.SingleToInt32Bits(a.Z) != BitConverter.SingleToInt32Bits(b.Z) || BitConverter.SingleToInt32Bits(a.W) != BitConverter.SingleToInt32Bits(b.W))
                    mismatches++;
            }
            info($"EvaluateMap parity: {compared} points ({withSystems} with systems), {mismatches} mismatches");
            check(mismatches == 0, $"EvaluateMap differs from the pre-extraction body at {mismatches} of {compared} points");
            check(withSystems > 150, "the parity sweep must have systems at most points");
        }

        // ---- c1. inactive: untriggered, and before the trigger time ----
        var rgba = new float[MegastormFarCoverage.Texels * 4];
        {
            var untriggered = new MegastormModel();
            layer.MegastormSource = untriggered;
            for (int i = 0; i < rgba.Length; i++) rgba[i] = 0.5f;
            bool active = MegastormFarCoverage.Build(layer, untriggered, t, Across[1], Vector3.Zero, rgba, out _, out _);
            check(!active && rgba.All(v => v == 0.0f), "untriggered Build must return false with a zero table");

            var late = new MegastormModel();
            late.Trigger(new MegastormTrigger("late", LeadAround, 1, t + 1000.0, 350.0f, 0.0f, 7));
            layer.MegastormSource = late;
            for (int i = 0; i < rgba.Length; i++) rgba[i] = 0.5f;
            active = MegastormFarCoverage.Build(layer, late, t, Across[1], Vector3.Zero, rgba, out _, out _);
            check(!active && rgba.All(v => v == 0.0f), "Build before the trigger time must return false with a zero table");
        }

        // ---- c2/c3. range, determinism, finite and bounded, T plateau ----
        layer.MegastormSource = model;
        (double rangeMin, double rangeMax) = MegastormFarCoverage.Range(model);
        info($"range xi {rangeMin / 1000.0:0.0} .. {rangeMax / 1000.0:0.0} km");
        check(rangeMin < -250_000.0 && rangeMax > 400_000.0, "range must cover P (-260 km) and the end of T (~390 km)");
        {
            var first = new float[rgba.Length];
            var second = new float[rgba.Length];
            bool a = MegastormFarCoverage.Build(layer, model, t, Across[1], Vector3.Zero, first, out double min1, out double max1);
            bool b = MegastormFarCoverage.Build(layer, model, t, Across[1], Vector3.Zero, second, out double min2, out double max2);
            check(a && b, "Build must be active for a triggered, formed megastorm");
            check(min1 == rangeMin && max1 == rangeMax && min2 == min1 && max2 == max1, "Build must report Range");
            bool same = true, bounded = true;
            for (int i = 0; i < first.Length; i++)
            {
                if (BitConverter.SingleToInt32Bits(first[i]) != BitConverter.SingleToInt32Bits(second[i])) same = false;
                if (!float.IsFinite(first[i]) || first[i] < 0.0f || first[i] > 1.0f) bounded = false;
            }
            check(same, "table is not deterministic across two builds");
            check(bounded, "table values must be finite and in [0, 1]");
            double step = (rangeMax - rangeMin) / MegastormFarCoverage.Texels;
            float plateauMin = 1.0f;
            for (int i = 0; i < MegastormFarCoverage.Texels; i++)
            {
                double xi = rangeMin + (i + 0.5) * step;
                if (xi >= 150_000.0 && xi <= 300_000.0) plateauMin = Math.Min(plateauMin, first[i * 4 + 1]);
            }
            info($"T plateau (xi 150..300 km) minimum mid {plateauMin:0.000}");
            check(plateauMin >= 0.9f, $"T plateau mid dips to {plateauMin:0.000} (limit 0.9)");
            check(first[1] < 0.2f && first[(MegastormFarCoverage.Texels - 1) * 4 + 1] < 0.05f, "table edges must be near zero");
        }

        // ---- b. composition with the natural map, two winds, three across positions ----
        {
            Vector2[] winds = { layer.WeatherWindMetersPerSecond, new Vector2(-3.1f, 1.6f) };
            float[] maxErr = new float[4];
            double[] maxErrXi = new double[4];
            int samples = 0;
            var tp = new float[rgba.Length];
            var natural = new List<RingVolumetricCloudLayer.RenderWeatherSystem>();
            var full = new List<RingVolumetricCloudLayer.RenderWeatherSystem>();
            var naturalLayer = new List<CloudLayerSystem>();
            var fullLayer = new List<CloudLayerSystem>();
            var extra = new List<RingVolumetricCloudLayer.RenderWeatherSystem>();
            foreach (Vector2 wind in winds)
            {
                layer.WeatherWindMetersPerSecond = wind;
                foreach (double across in Across)
                {
                    MegastormFarCoverage.Build(layer, model, t, across, Vector3.Zero, tp, out double xiMin, out double xiMax);
                    double step = (xiMax - xiMin) / MegastormFarCoverage.Texels;
                    for (double xi = xiMin - 50_000.0; xi <= xiMax + 50_000.0; xi += 5_000.0)
                    {
                        RingCanonicalPosition p = PointAtXi(model, xi, across, t);
                        natural.Clear();
                        layer.CollectRenderWeatherSystems(p, 250_000.0, t, natural, 0.0f, 500.0f, includeMegastorm: false);
                        extra.Clear();
                        layer.CollectMegastormRenderSystems(p, circumference, t, extra, 0.0f, 500.0f);
                        full.Clear();
                        full.AddRange(natural);
                        foreach (var s in extra)
                            if (s.SystemId == MegastormModel.TrailingRecordId || s.SystemId == MegastormModel.PrecursorRecordId) full.Add(s);
                        ToLayerSystems(natural, naturalLayer, _ => true);
                        ToLayerSystems(full, fullLayer, _ => true);
                        CloudLayerPoint point = MakePoint(layer, p.AroundMeters, p.AcrossMeters, t, 0.3f, circumferenceF);
                        Vector4 nat = RingCloudLayers.EvaluateMap(point, naturalLayer, table);
                        Vector4 expected = RingCloudLayers.EvaluateMap(point, fullLayer, table);
                        // Interpolated table value (0 outside the range).
                        Vector4 lut = Vector4.Zero;
                        if (xi >= xiMin && xi <= xiMax)
                        {
                            double f = (xi - xiMin) / step - 0.5;
                            int i0 = Math.Clamp((int)Math.Floor(f), 0, MegastormFarCoverage.Texels - 1);
                            int i1 = Math.Min(i0 + 1, MegastormFarCoverage.Texels - 1);
                            float w = (float)Math.Clamp(f - i0, 0.0, 1.0);
                            Vector4 v0 = new(tp[i0 * 4], tp[i0 * 4 + 1], tp[i0 * 4 + 2], tp[i0 * 4 + 3]);
                            Vector4 v1 = new(tp[i1 * 4], tp[i1 * 4 + 1], tp[i1 * 4 + 2], tp[i1 * 4 + 3]);
                            lut = v0.Lerp(v1, w);
                        }
                        Vector4 composed = new(1.0f - (1.0f - nat.X) * (1.0f - lut.X), 1.0f - (1.0f - nat.Y) * (1.0f - lut.Y),
                            1.0f - (1.0f - nat.Z) * (1.0f - lut.Z), MathF.Max(nat.W, lut.W));
                        samples++;
                        float[] err = { MathF.Abs(composed.X - expected.X), MathF.Abs(composed.Y - expected.Y), MathF.Abs(composed.Z - expected.Z), MathF.Abs(composed.W - expected.W) };
                        for (int c = 0; c < 4; c++)
                            if (err[c] > maxErr[c]) { maxErr[c] = err[c]; maxErrXi[c] = xi; }
                    }
                }
            }
            info($"composition over {samples} samples: max error low {maxErr[0]:0.0000} (xi {maxErrXi[0] / 1000.0:0.0} km), mid {maxErr[1]:0.0000} (xi {maxErrXi[1] / 1000.0:0.0}), "
                + $"high {maxErr[2]:0.0000} (xi {maxErrXi[2] / 1000.0:0.0}), weatherness {maxErr[3]:0.0000} (xi {maxErrXi[3] / 1000.0:0.0})");
            check(maxErr[0] <= 0.06f, $"composition low error {maxErr[0]:0.0000} at xi {maxErrXi[0] / 1000.0:0.0} km (limit 0.06)");
            check(maxErr[1] <= 0.02f, $"composition mid error {maxErr[1]:0.0000} at xi {maxErrXi[1] / 1000.0:0.0} km (limit 0.02)");
            check(maxErr[2] <= 0.02f, $"composition high error {maxErr[2]:0.0000} at xi {maxErrXi[2] / 1000.0:0.0} km (limit 0.02)");
            check(maxErr[3] <= 0.02f, $"composition weatherness error {maxErr[3]:0.0000} at xi {maxErrXi[3] / 1000.0:0.0} km (limit 0.02)");
        }
    }

    // ---- verbatim copy of RingCloudLayers.EvaluateMap before the AccumulateSystems extraction ----
    static float SmoothUnit(float value)
    {
        float t = Math.Clamp(value, 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    static float RampDown(float s, float from, float to) => SmoothUnit((from - s) / (from - to));

    static float Wrap(float delta, float circumference)
        => circumference > 1.0f ? delta - circumference * MathF.Floor(delta / circumference + 0.5f) : delta;

    static Vector4 OldEvaluateMap(in CloudLayerPoint point, IReadOnlyList<CloudLayerSystem> systems, CloudLayerTable table)
    {
        float circumference = point.CircumferenceMeters;
        float sourceAround = point.CanonicalAround + point.Shift.X;
        float sourceAcross = point.CanonicalAcross + point.Shift.Y;
        float windX = MathF.Cos(point.WindAngleRadians);
        float windY = MathF.Sin(point.WindAngleRadians);
        Vector2 densityRamp = table.WeathernessDensityRamp;

        float remainingLow = 1.0f;
        float remainingMid = 1.0f;
        float remainingHigh = 1.0f;
        float weatherness = 0.0f;
        for (int i = 0; i < systems.Count; i++)
        {
            RingVolumetricCloudLayer.RenderWeatherSystem system = systems[i].System;
            float fade = systems[i].Fade;
            CloudGenusRow? row = table.Genus(system.PhysicalProfile);
            bool lowersBase = (system.Flags & RingVolumetricCloudLayer.RenderWeatherFlagLowersBase) != 0u;
            float majorRadius = system.MajorRadiusMeters;
            float minorRadius = system.MinorRadiusMeters;
            float density = system.Influence;
            float influenceLow;
            float influenceUpper;
            float frontLow = 1.0f, frontMid = 1.0f, frontHigh = 1.0f, frontWeather = 1.0f;
            if (system.IsStatic)
            {
                float deltaAround = Wrap(point.CanonicalAround - (float)system.CenterAroundMeters, circumference);
                float deltaAcross = point.CanonicalAcross - (float)system.CenterAcrossMeters;
                float x = deltaAround * system.OrientationCosine + deltaAcross * system.OrientationSine;
                float z = -deltaAround * system.OrientationSine + deltaAcross * system.OrientationCosine;
                float normalized = MathF.Sqrt(x * x / (majorRadius * majorRadius) + z * z / (minorRadius * minorRadius));
                influenceLow = (1.0f - SmoothUnit((normalized - 0.55f) / 0.75f)) * density * fade;
                if (influenceLow <= 0.0001f) continue;
                influenceUpper = influenceLow;
            }
            else
            {
                float support = MathF.Max(1.0f, system.SupportMultiplier);
                float deltaAround = Wrap((float)system.CenterAroundMeters - sourceAround, circumference);
                float deltaAcross = (float)system.CenterAcrossMeters - sourceAcross;
                float reach = majorRadius * support;
                if (deltaAround * deltaAround + deltaAcross * deltaAcross >= reach * reach) continue;
                float along = system.OrientationCosine * deltaAround + system.OrientationSine * deltaAcross;
                float across = -system.OrientationSine * deltaAround + system.OrientationCosine * deltaAcross;
                float normalized = MathF.Sqrt(along * along / MathF.Max(1.0f, majorRadius * majorRadius)
                    + across * across / MathF.Max(1.0f, minorRadius * minorRadius));
                if (normalized >= support) continue;
                influenceLow = normalized >= 1.0f ? 0.0f : (1.0f - SmoothUnit((normalized - 0.32f) / 0.68f)) * density * fade;
                influenceUpper = (1.0f - SmoothUnit((normalized / support - 0.32f) / 0.68f)) * density * fade;
                if (row != null && row.FrontType != CloudFrontType.None)
                {
                    float systemX = -along;
                    float downwindX = system.OrientationCosine * windX + system.OrientationSine * windY;
                    float downwindY = -system.OrientationSine * windX + system.OrientationCosine * windY;
                    float systemY = -across;
                    float q = systemX * downwindX + systemY * downwindY;
                    float s = q * MathF.Sqrt(downwindX * downwindX / (majorRadius * majorRadius)
                        + downwindY * downwindY / (minorRadius * minorRadius));
                    if (row.FrontType == CloudFrontType.Warm)
                    {
                        float lowRamp = RampDown(s, 0.5f, 0.0f);
                        float behind = SmoothUnit((-0.6f - s) / 0.4f);
                        frontHigh = RampDown(s, 2.0f, 0.8f);
                        frontMid = RampDown(s, 1.2f, 0.3f);
                        frontLow = lowRamp * (1.0f + (0.6f - 1.0f) * behind);
                        frontWeather = lowRamp * (1.0f - behind);
                    }
                    else
                    {
                        float band = 1.0f - SmoothUnit((MathF.Abs(s) - 0.4f) / 0.25f);
                        frontLow = band;
                        frontMid = band;
                        frontWeather = band;
                        frontHigh = RampDown(s, 1.6f, 0.2f);
                    }
                }
            }

            if (row != null)
            {
                remainingLow *= 1.0f - Math.Clamp(row.Low * frontLow * influenceLow, 0.0f, 1.0f);
                remainingMid *= 1.0f - Math.Clamp(row.Mid * frontMid * influenceUpper, 0.0f, 1.0f);
                remainingHigh *= 1.0f - Math.Clamp(row.High * frontHigh * influenceUpper, 0.0f, 1.0f);
            }
            if (lowersBase)
            {
                float densityGate = Math.Clamp((density - densityRamp.X) / MathF.Max(densityRamp.Y, 0.0001f), 0.0f, 1.0f);
                weatherness = MathF.Max(weatherness, influenceLow * densityGate * frontWeather);
            }
        }
        weatherness = Math.Clamp(weatherness, 0.0f, 1.0f);

        float diurnal = RingCloudLayers.DiurnalCoverageFactor(point.CanonicalAround, circumference, point.SunLocal, table);
        float lowFair = point.Coverage * (diurnal + (1.0f - diurnal) * weatherness);
        float midFair = Math.Clamp(point.BaseCoverage + point.ClusterMid, 0.015f, 0.985f) * table.MidFairScale;
        float highFair = Math.Clamp(point.BaseCoverage + point.ClusterHigh, 0.015f, 0.985f) * table.HighFairScale;
        lowFair = RingCloudLayers.ApplyAmountBias(lowFair, table.LowAmountBias);
        midFair = RingCloudLayers.ApplyAmountBias(midFair, table.MidAmountBias);
        highFair = RingCloudLayers.ApplyAmountBias(highFair, table.HighAmountBias);
        return new Vector4(
            Math.Clamp(1.0f - (1.0f - lowFair) * remainingLow, 0.0f, 1.0f),
            Math.Clamp(1.0f - (1.0f - midFair) * remainingMid, 0.0f, 1.0f),
            Math.Clamp(1.0f - (1.0f - highFair) * remainingHigh, 0.0f, 1.0f),
            weatherness);
    }
}
