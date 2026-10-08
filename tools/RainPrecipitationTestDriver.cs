using System;
using Godot;
using RingworldTechDemo.Weather;
using RingworldTechDemo.Diagnostics;

namespace RingworldTechDemo.Tests;

/// <summary>Disposable-scene fixture adapter for the existing typed override API.</summary>
public partial class RainPrecipitationTestDriver : RefCounted
{
    private CompositorEffect? _heldRainCompositor;

    public void ConfigureRainGpuProfilerForFixture(bool enabled)
    {
        RingPerformanceTelemetry.Configure(enabled, 1024);
        RingPerformanceGpu.Configure(enabled);
    }

    public void RequestRainGpuReadbackForFixture() => RingPerformanceGpu.RequestReadback();

    public Godot.Collections.Dictionary DrainRainGpuSamplesForFixture()
    {
        var samples = new RingPerformanceTelemetry.Completed[1024];
        int count = RingPerformanceTelemetry.DrainGpu(samples, out int dropped), rainCount = 0;
        long sourceFrame = -1;
        double maximum = 0;
        for (int i = 0; i < count; i++)
        {
            var sample = samples[i];
            if (sample.Name != "GPU.RainHaze") continue;
            if (sample.SourceFrameId < 0 || !double.IsFinite(sample.Milliseconds) || sample.Milliseconds <= 0)
                throw new InvalidOperationException("Invalid actual rain GPU timestamp sample.");
            rainCount++; sourceFrame = Math.Max(sourceFrame, sample.SourceFrameId);
            maximum = Math.Max(maximum, sample.Milliseconds);
        }
        return new() { ["count"] = rainCount, ["source_frame"] = sourceFrame, ["maximum_ms"] = maximum, ["dropped"] = dropped };
    }

    public void HoldRainCompositorForFixture(Node local, bool hold)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(RingLocalWeatherEffects);
        var field = type.GetField("_rainHazeCompositor", flags)!;
        if (hold)
        {
            if (_heldRainCompositor != null) throw new InvalidOperationException("Rain compositor already held.");
            _heldRainCompositor = (CompositorEffect?)field.GetValue(local);
            type.GetMethod("DetachRainHazeCompositor", flags)!.Invoke(local, null);
            field.SetValue(local, null);
        }
        else
        {
            field.SetValue(local, _heldRainCompositor);
            _heldRainCompositor = null;
        }
        type.GetMethod("UpdateRainHaze", flags)!.Invoke(local, null);
    }

    public void PublishHazeWindAnchorForFixture(Node world, Vector3 anchor)
    {
        var weather = world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds");
        weather.SharedWindField.SetCached("ring_wind_anchor_world", anchor);
        var local = world.GetNode<RingLocalWeatherEffects>("LocalWeatherEffects");
        typeof(RingLocalWeatherEffects).GetMethod("UpdateRainHaze", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(local, null);
    }
    public Vector3 PublishedWindAnchor(Node world)
    {
        var grid = world.GetNode<RingworldTechDemo.Voxel.Phase2.VoxelChunkGridManager>("ChunkGridManager");
        var explorer = world.GetNode<RingworldTechDemo.Voxel.Integration.RingVoxelExplorerController>("Explorer");
        var position = explorer.CanonicalPosition;
        return grid.CaptureLodGeometrySnapshot().CanonicalToWorld(position.AroundMeters, position.VerticalMeters, position.AcrossMeters);
    }

    public bool CheckProfileMath()
    {
        RainPrecipitationProfile previous = RainPrecipitationProfile.Resolve(0);
        for (int sample = 0; sample <= 10000; sample++)
        {
            float intensity = sample / 10000.0f;
            RainPrecipitationProfile p = RainPrecipitationProfile.Resolve(intensity);
            if (Math.Abs(p.ScatteredWeight + p.SteadyWeight + p.HeavyWeight - 1) > .000001f
                || p.ScatteredWeight < 0 || p.SteadyWeight < 0 || p.HeavyWeight < 0
                || Math.Abs(p.ScatteredWeight - previous.ScatteredWeight) > .001f
                || Math.Abs(p.SteadyWeight - previous.SteadyWeight) > .001f
                || Math.Abs(p.HeavyWeight - previous.HeavyWeight) > .001f)
                throw new InvalidOperationException($"Invalid rain blend at {intensity}: {p}");
            previous = p;
        }
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1.0f })
            if (RainPrecipitationProfile.Resolve(invalid) != RainPrecipitationProfile.Resolve(0))
                throw new InvalidOperationException("Invalid intensity did not resolve to the dry profile.");
        if (RainPrecipitationProfile.Resolve(2) != RainPrecipitationProfile.Resolve(1))
            throw new InvalidOperationException("Rain profile exceeded its upper intensity bound.");
        foreach (var (intensity, name) in new[] { (0.0f, "Clear"), (.15f, "Scattered"), (.5f, "Steady"), (1.0f, "Heavy") })
        {
            RainPrecipitationProfile p = RainPrecipitationProfile.Resolve(intensity);
            if (p.Stage(intensity, false) != name || p.Stage(intensity, true) != "Snow")
                throw new InvalidOperationException("Unexpected rain stage label.");
        }
        GD.Print("RAIN_PRECIPITATION_PROFILE_MATH=PASS samples=10001");
        return true;
    }

    public Variant GetHazeControl(string key) => RingRainHazeSettings.Current.Get(key);
    public void SetDaylightForFixture(Node cycle, float daylight) => cycle.GetType().GetProperty("DaylightFactor")!.SetValue(cycle, daylight);
    public void SetHazeControl(string key, Variant value) => RingRainHazeSettings.Current.Set(key, value);
    public float HazeOpacity(float intensity, float distance) => RingLocalWeatherEffects.EvaluateRainHazeOpacity(intensity, distance);

    public void SetUnderwaterForFixture(Node explorer, bool underwater)
        => explorer.GetType().GetProperty("IsCameraUnderwater")!.SetValue(explorer, underwater);
    public void SetShelterForFixture(Node ambience, bool sheltered)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        ambience.GetType().GetField("_coverTarget", flags)!.SetValue(ambience, sheltered ? .82f : 0f);
        ambience.GetType().GetField("_deepShelter", flags)!.SetValue(ambience, false);
    }

    public void SetIntensity(Node effects, float intensity, bool snow, Vector2 wind)
    {
        if (effects is not RingLocalWeatherEffects local)
            throw new ArgumentException("Expected the production local weather effects node.");
        float target = float.IsFinite(intensity) ? Mathf.Clamp(intensity, 0, 1) : 0;
        // Invert the publisher's existing smoothstep, preserving its normal
        // threshold and rise/fall smoothing rather than assigning private state.
        float lower = 0, upper = 1;
        for (int iteration = 0; iteration < 24; iteration++)
        {
            float middle = (lower + upper) * .5f;
            if (middle * middle * (3 - 2 * middle) < target) lower = middle;
            else upper = middle;
        }
        float raw = local.PrecipitationStart + (1 - local.PrecipitationStart) * ((lower + upper) * .5f);
        if (target == 0) raw = 0;
        if (target == 1) raw = 1;
        local.SnowGuaranteedAboveAltitudeMeters = 1_000_000;
        local.SnowTemperatureThreshold = snow ? 100 : -100;
        local.SetExternalWeatherOverride(true, new RingVolumetricCloudLayer.LocalWeatherState(
            -9511, RingVolumetricCloudLayer.WeatherSystemKind.MajorFront, 1,
            10_000, Vector2.Zero, new Vector2(10_000, 10_000), 0,
            1, raw, wind, CloudFormationProfile.Cumulonimbus, 2000, 6000));
    }
}
