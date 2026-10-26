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

    /// <summary>Drains the asynchronous GPU timestamp samples and returns the milliseconds of every sample of one category (for example GPU.RainStreaks).</summary>
    public Godot.Collections.Array<double> DrainGpuMillisecondsForFixture(string name)
    {
        var samples = new RingPerformanceTelemetry.Completed[1024];
        int count = RingPerformanceTelemetry.DrainGpu(samples, out _);
        var result = new Godot.Collections.Array<double>();
        for (int i = 0; i < count; i++)
            if (samples[i].Name == name && double.IsFinite(samples[i].Milliseconds) && samples[i].Milliseconds > 0) result.Add(samples[i].Milliseconds);
        return result;
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

    /// <summary>World-space frame at the explorer: position, around/across/up axes and canonical coordinates (fixtures aim cameras and place geometry with it).</summary>
    public Godot.Collections.Dictionary FrameAxes(Node world)
    {
        var grid = world.GetNode<RingworldTechDemo.Voxel.Phase2.VoxelChunkGridManager>("ChunkGridManager");
        var explorer = world.GetNode<RingworldTechDemo.Voxel.Integration.RingVoxelExplorerController>("Explorer");
        var position = explorer.CanonicalPosition;
        var geometry = grid.CaptureLodGeometrySnapshot();
        Vector3 origin = geometry.CanonicalToWorld(position.AroundMeters, position.VerticalMeters, position.AcrossMeters);
        return new()
        {
            ["origin"] = origin,
            ["around"] = (geometry.CanonicalToWorld(position.AroundMeters + 10, position.VerticalMeters, position.AcrossMeters) - origin).Normalized(),
            ["across"] = (geometry.CanonicalToWorld(position.AroundMeters, position.VerticalMeters, position.AcrossMeters + 10) - origin).Normalized(),
            ["up"] = (geometry.CanonicalToWorld(position.AroundMeters, position.VerticalMeters + 10, position.AcrossMeters) - origin).Normalized(),
            ["around_m"] = position.AroundMeters, ["across_m"] = position.AcrossMeters, ["vertical_m"] = position.VerticalMeters
        };
    }

    /// <summary>Registers a manual storm whose centre is offset along the around axis from the explorer (storm-edge fixture).</summary>
    public void RegisterEdgeStorm(Node world, double aroundOffset, float radius)
    {
        var explorer = world.GetNode<RingworldTechDemo.Voxel.Integration.RingVoxelExplorerController>("Explorer");
        var position = explorer.CanonicalPosition;
        world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds").RegisterManualWeatherAt("storm_edge_fixture", position.AroundMeters + aroundOffset, position.AcrossMeters, radius);
    }
    public void UnregisterEdgeStorm(Node world) => world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds").UnregisterManualWeather("storm_edge_fixture");

    public void SetLightningPulseForFixture(Node local, float pulse, float weight)
    {
        var type = typeof(RingLocalWeatherEffects);
        type.GetProperty("CurrentRenderedLightningPulse")!.GetSetMethod(true)!.Invoke(local, new object[] { pulse });
        type.GetProperty("CurrentRenderedLightningVisualWeight")!.GetSetMethod(true)!.Invoke(local, new object[] { weight });
    }
    public void SetStreakFieldForFixture(Node local, float[] values) => ((RingLocalWeatherEffects)local).StreakFieldOverride = values.Length == 25 ? values : null;
    public void SetObserverOffsetForFixture(Node world, Node local, double aroundOffset, double acrossOffset)
    {
        var p = world.GetNode<RingworldTechDemo.Voxel.Integration.RingVoxelExplorerController>("Explorer").CanonicalPosition;
        ((RingLocalWeatherEffects)local).StreakObserverOverride = new RingworldTechDemo.Voxel.Core.RingCanonicalPosition(p.AroundMeters + aroundOffset, p.VerticalMeters, p.AcrossMeters + acrossOffset);
    }
    public void SetStormLevelForFixture(Node local, float level) => ((RingLocalWeatherEffects)local).StormLevelOverride = level < 0 ? null : level;
    public void SetWindSettingsForFixture(float gustStrength, float gustBase, float gustScale = 600f)
    {
        RingWindSettings.Current.GustStrength = gustStrength; RingWindSettings.Current.GustBaseBreeze = gustBase; RingWindSettings.Current.GustScaleMeters = gustScale;
    }
    public void SetStreakSettingForFixture(string key, float value) => RingRainHazeSettings.Current.Set(key, value);
    /// <summary>S3 fixture: replaces the storm arrival state the streaks and the haze gate read (all weights 0..1; clear = true restores the observer's own state).</summary>
    public void SetStreakArrivalForFixture(Node effects, float fatDrop, float core, float trailing, float severity, float hail, float hailSize, bool clear)
    {
        var local = (RingLocalWeatherEffects)effects;
        local.StreakArrivalOverride = clear ? null : RingworldTechDemo.Weather.StormArrivalState.Calm with
        {
            Valid = true, FatDrop = fatDrop, Core = core, Trailing = trailing, Severity = severity, HailIntensity = hail, HailSize = hailSize,
            Phase = RingworldTechDemo.Weather.StormArrivalPhase.Core
        };
    }
    /// <summary>Capture hook: switches the S2 screen-space lens droplets off so rain captures judge the S1 streaks alone.</summary>
    public void SetStreakGridMirrorForFixture(Node local, bool enabled) => ((RingLocalWeatherEffects)local).StreakGridMirrorEnabled = enabled;
    public void SetLensDropletsForFixture(float amount) => RingWetWorldSettings.Current.LensDroplets = amount;

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

    /// <summary>Pure checks of the streak model: tier budgets, density/length/brightness monotonic in intensity, world-anchored double-precision phase, wind slope, haze onset.</summary>
    public bool CheckStreakModel()
    {
        void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("STREAK_MODEL: " + message); }
        int[] budgets = { 2000, 4000, 6000, 8000, 12000 };
        int previousCells = 0;
        foreach (int budget in budgets)
        {
            RainStreakLattice.Layers layers = RainStreakLattice.Resolve(budget);
            int cells = layers.NearCells + layers.MidCells;
            Require(cells >= previousCells && Math.Abs(cells - budget) < budget * .15f, $"cells {cells} for budget {budget}");
            Require(Math.Abs(layers.NearCellsPerSide * layers.NearCellMeters - 2 * RainStreakLattice.NearRadiusMeters) < .001f && Math.Abs(layers.MidCellsPerSide * layers.MidCellMeters - 2 * RainStreakLattice.MidRadiusMeters) < .001f, "lattice does not span its radius");
            previousCells = cells;
        }
        float density = -1, length = -1, brightness = -1, mid = -1;
        for (int i = 0; i <= 1000; i++)
        {
            float intensity = i / 1000.0f;
            RainPrecipitationProfile p = RainPrecipitationProfile.Resolve(intensity);
            float d = RainStreakLattice.Density(intensity);
            Require(d >= density - 1e-5f && p.LengthScale >= length - 1e-5f && p.BrightnessScale >= brightness - 1e-5f && p.MidLayerWeight >= mid - 1e-5f, $"tier not monotonic at {intensity}");
            density = d; length = p.LengthScale; brightness = p.BrightnessScale; mid = p.MidLayerWeight;
        }
        Require(RainStreakLattice.Density(0) == 0 && Math.Abs(RainStreakLattice.Density(1) - 1) < 1e-5f && RainStreakLattice.Density(float.NaN) == 0, "density endpoints");
        // Phase: a drop column's absolute height (hash*P - T*v) is independent of where the observer stands, also at huge clock values.
        foreach (float period in new[] { RainStreakLattice.NearPeriodMeters, RainStreakLattice.MidPeriodMeters })
        foreach (double time in new[] { 0.0, 12.5, 7777.125, 3.0e7 + .25 })
            foreach (double vertical in new[] { 0.0, 1.7, 250.0, 4999.5, -35.0 })
            {
                float phase = RainStreakLattice.FallPhase(time, vertical, period);
                Require(phase >= 0 && phase < period && phase == RainStreakLattice.FallPhase(time, vertical, period), "phase range/determinism");
                foreach (float hash in new[] { 0.0f, .31f, .999f })
                {
                    double absolute = ((hash * period - time * RainStreakLattice.FallSpeedMetersPerSecond) % period + period) % period;
                    double recovered = ((RainStreakLattice.RelativeHeight(hash, phase, period) + vertical) % period + period) % period;
                    double delta = Math.Abs(absolute - recovered); delta = Math.Min(delta, period - delta);
                    Require(delta < .02, $"drop not world anchored: t={time} v={vertical} h={hash} delta={delta}");
                    float relative = RainStreakLattice.RelativeHeight(hash, phase, period);
                    Require(relative >= -period * .5f - 1e-3f && relative < period * .5f + 1e-3f, "relative height out of band");
                }
            }
        // Drops fall: one second later the same column is lower by the fall speed.
        const float pm = RainStreakLattice.MidPeriodMeters; float before = RainStreakLattice.RelativeHeight(.4f, RainStreakLattice.FallPhase(10, 0, pm), pm), after = RainStreakLattice.RelativeHeight(.4f, RainStreakLattice.FallPhase(10.1, 0, pm), pm);
        Require(Math.Abs((before - after) - RainStreakLattice.FallSpeedMetersPerSecond * .1f) < .02f || Math.Abs((before - after) + pm - RainStreakLattice.FallSpeedMetersPerSecond * .1f) < .02f, "drop does not fall at the fall speed");
        float slopePrevious = -1;
        for (int speed = 0; speed <= 60; speed += 3)
        {
            Vector2 slope = RainStreakLattice.Slope(new Vector2(speed, 0));
            Require(slope.Length() >= slopePrevious - 1e-5f && slope.Length() <= RainStreakLattice.MaximumSlope + 1e-5f && slope.Y == 0, "slope not monotonic/capped");
            slopePrevious = slope.Length();
        }
        Require(RainStreakLattice.Slope(new Vector2(6, 0)).X > 0 && RainStreakLattice.Slope(new Vector2(-6, 8)).Y > 0 && RainStreakLattice.Slope(new Vector2(float.NaN, 0)) == Vector2.Zero, "slope direction");
        // Haze is reserved for violent storms: nothing below the onset, full only for severe systems at full intensity, smooth and monotonic.
        float hazePrevious = -1, severePrevious = -1;
        for (int i = 0; i <= 1000; i++)
        {
            float intensity = i / 1000.0f;
            float normal = RingLocalWeatherEffects.RainHazeOnsetFactor(intensity, false, .78f), severe = RingLocalWeatherEffects.RainHazeOnsetFactor(intensity, true, .78f);
            Require(normal >= hazePrevious - 1e-5f && severe >= severePrevious - 1e-5f && severe >= normal - 1e-5f && normal is >= 0 and <= 1, "haze onset not monotonic");
            if (intensity <= .6f) Require(normal == 0 && severe == 0, $"light/moderate rain has haze at {intensity}");
            hazePrevious = normal; severePrevious = severe;
        }
        Require(RingLocalWeatherEffects.RainHazeOnsetFactor(1, true, .78f) > .999f && RingLocalWeatherEffects.RainHazeOnsetFactor(1, false, .78f) < .95f && RingLocalWeatherEffects.RainHazeOnsetFactor(.5f, true, .78f) == 0, "haze onset endpoints");
        Require(RingLocalWeatherEffects.RainHazeOnsetFactor(1, false, 0) > .99f && RingLocalWeatherEffects.RainHazeOnsetFactor(float.NaN, true, .78f) == 0, "haze onset tunable/NaN");
        // S3 arrival hooks: all weights 0 are the identity; the core bias, trailing cap, fat fraction and the heavy-core veil rule (Core x Severity >= 0.7).
        for (int i = 0; i <= 100; i++)
        {
            float v = i / 100.0f;
            Require(RainPrecipitationProfile.ArrivalTierIntensity(v, 0, 0, 0) == v && RainPrecipitationProfile.ArrivalTierIntensity(v, 1, 0, 0) == v, "arrival tier hooks must be the identity at weight 0 / severity 0");
            Require(RainPrecipitationProfile.ArrivalTierIntensity(v, 1, 1, 0) >= v - 1e-6f && RainPrecipitationProfile.ArrivalTierIntensity(v, 1, 1, 0) <= Math.Min(1.0f, v + .15f + 1e-5f), "core bias is at most +0.15");
            Require(RainPrecipitationProfile.ArrivalTierIntensity(v, 0, 0, 1) <= RainPrecipitationProfile.TrailingTierCap + 1e-6f && RainPrecipitationProfile.ArrivalTierIntensity(v, 0, 0, 1) <= v + 1e-6f, "trailing caps the tier at Steady");
        }
        Require(RainPrecipitationProfile.ArrivalTierIntensity(0f, 1, 1, 0) == 0f, "core bias never creates rain from a dry field");
        Require(RainPrecipitationProfile.ArrivalTierIntensity(float.NaN, 1, 1, 1) == 0f, "arrival tier NaN");
        Require(RainPrecipitationProfile.ArrivalLengthScale(0) == 1f && Math.Abs(RainPrecipitationProfile.ArrivalLengthScale(1) - .85f) < 1e-6f, "trailing length x0.85");
        Require(RainPrecipitationProfile.FatDropFraction(0) == 0f && Math.Abs(RainPrecipitationProfile.FatDropFraction(1) - .10f) < 1e-6f && RainPrecipitationProfile.FatDropFraction(1) is >= .04f and <= .12f, "fat drop floor density 0.04-0.12");
        Require(RainPrecipitationProfile.HeavyCoreWeight(1f, .7f) > .999f && RainPrecipitationProfile.HeavyCoreWeight(1f, .59f) == 0f && RainPrecipitationProfile.HeavyCoreWeight(.5f, 1f) == 0f && RainPrecipitationProfile.HeavyCoreWeight(1f, 1.5f) > .999f, "heavy core rule: Core x Severity >= 0.7");
        float gatePrevious = -1;
        for (int i = 0; i <= 100; i++)
        {
            float gate = RingLocalWeatherEffects.RainHazeOnsetFactor(.95f, RainPrecipitationProfile.HeavyCoreWeight(1f, i / 100.0f * 1.2f), .78f);
            Require(gate >= gatePrevious - 1e-6f, "veil gate must not pop as Core x Severity rises"); gatePrevious = gate;
        }
        Require(Math.Abs(RingLocalWeatherEffects.RainHazeOnsetFactor(.9f, 1.0f, .78f) - RingLocalWeatherEffects.RainHazeOnsetFactor(.9f, true, .78f)) < 1e-6f && Math.Abs(RingLocalWeatherEffects.RainHazeOnsetFactor(.9f, 0.0f, .78f) - RingLocalWeatherEffects.RainHazeOnsetFactor(.9f, false, .78f)) < 1e-6f, "bool and weight overloads agree at the endpoints");
        Require(RainStreakLattice.HailPhase(10, 0, RainStreakLattice.HailNearPeriodMeters) is >= 0f and < RainStreakLattice.HailNearPeriodMeters && Math.Abs(RainStreakLattice.HailPhase(0, 5, 36f) - 5f) < 1e-4f, "hail phase");
        foreach (string key in new[] { "HazeOnset", "StreakDensity", "StreakBrightness", "StreakLength", "SplashAmount" })
        {
            Require(UI.GameplayRainHazeDefaults.Limits.TryGetValue(key, out var range), key + " missing from the persisted limits");
            float value = RingRainHazeSettings.Current.Get(key).AsSingle();
            Require(value >= range.Min && value <= range.Max, key + " default out of range");
            Require(UI.GameplayRainHazeDefaults.TryValidate(key, 1000f, out Variant clamped) && clamped.AsSingle() == range.Max && !UI.GameplayRainHazeDefaults.TryValidate(key, float.NaN, out _), key + " sanitise");
        }
        GD.Print("RAIN_STREAK_MODEL=PASS tiers=5 phase=anchored slope=capped haze_onset=smooth settings=5");
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

    /// <summary>Real precipitation potential of the cloud layer at an offset along the around axis from the explorer (storm-edge fixture).</summary>
    public float PotentialAt(Node world, double aroundOffset)
    {
        var explorer = world.GetNode<RingworldTechDemo.Voxel.Integration.RingVoxelExplorerController>("Explorer");
        var p = explorer.CanonicalPosition;
        return world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds").QueryLocalWeather(new RingworldTechDemo.Voxel.Core.RingCanonicalPosition(p.AroundMeters + aroundOffset, p.VerticalMeters, p.AcrossMeters)).PrecipitationPotential;
    }

    /// <summary>Drives the local effects with a raw precipitation potential (not an intensity), so a storm-edge fixture matches the cloud layer's own query at the explorer.</summary>
    public void SetPotentialOverride(Node effects, float potential, Vector2 wind)
    {
        var local = (RingLocalWeatherEffects)effects;
        local.SnowGuaranteedAboveAltitudeMeters = 1_000_000;
        local.SnowTemperatureThreshold = -100;
        local.SetExternalWeatherOverride(true, new RingVolumetricCloudLayer.LocalWeatherState(
            -9511, RingVolumetricCloudLayer.WeatherSystemKind.MajorFront, 1,
            10_000, Vector2.Zero, new Vector2(10_000, 10_000), 0,
            1, potential, wind, CloudFormationProfile.Cumulonimbus, 2000, 6000));
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
