using System;
using Godot;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Weather;

/// <summary>
/// Drives the post-rain mist of the physical haze (docs/storm-revamp-s3-storm-arrival-design.md section 5, WO-E1): M = PostRainMist x Clearing x calm x cool,
/// with calm = 1 - smoothstep(3, 8 m/s, wind) and cool = smoothstep(0, -0.1, CoolingOffset). The strength is published to RingAtmosphereModel.SetWeatherMist, which
/// quantises, slew-limits and rate-limits it. No GPU work of its own; at 0 the haze is exactly unchanged. Evaluated at 4 Hz.
/// </summary>
public partial class RingPostRainMist : Node
{
    [Export] public NodePath WeatherLayerPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath WeatherEffectsPath { get; set; } = new("../LocalWeatherEffects");
    [Export] public NodePath MegastormPath { get; set; } = new("../RingMegastorm");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public bool AutoBind { get; set; } = true;
    [Export(PropertyHint.Range, "0.05,2,0.05")] public float UpdateSeconds { get; set; } = 0.25f;

    private RingVolumetricCloudLayer? _layer;
    private RingLocalWeatherEffects? _effects;
    private RingMegastorm? _megastorm;
    private RingVoxelExplorerController? _explorer;
    private double _accumulator;
    private bool _wakeHeld;
    /// <summary>Wake steps per publish: 0 to 1 in 8 publishes (about 16 s at the 2 s spacing; 32 s at the post-rain 2 steps would exceed the 30-per-10-min cap over a round trip).</summary>
    public const int WakeStepsPerPublish = 4;
    /// <summary>Test seam: replaces the observer's MegastormHazards.WakeFog.</summary>
    public Func<float>? WakeFogProvider { get; set; }
    /// <summary>Last wake fog (megastorm low-pressure fog at the observer), for diagnostics.</summary>
    public float LastWakeFog { get; private set; }
    /// <summary>Last target M (before quantisation), for diagnostics.</summary>
    public float TargetMist { get; private set; }
    public float LastClearing { get; private set; }
    public float LastCalm { get; private set; } = 1.0f;
    public float LastCool { get; private set; }

    public override void _Ready()
    {
        if (!AutoBind) return;
        _layer = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherLayerPath);
        _effects = GetNodeOrNull<RingLocalWeatherEffects>(WeatherEffectsPath);
        _megastorm = GetNodeOrNull<RingMegastorm>(MegastormPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
    }

    public void Bind(RingVolumetricCloudLayer? layer, RingLocalWeatherEffects? effects) { _layer = layer; _effects = effects; }

    /// <summary>The mist strength for the given inputs (pure; used by the process step and the tests).</summary>
    public static float Target(float setting, float clearing, float windSpeed, float coolingOffset)
    {
        float calm = 1.0f - StormArrivalMath.SmoothStep(3.0f, 8.0f, windSpeed);
        float cool = StormArrivalMath.SmoothStep(0.0f, -0.1f, coolingOffset);
        // Clearing peaks around 0.75 in practice, so it is scaled by 1.5 (saturating) for the "full strength" of the setting to be reachable.
        return Mathf.Clamp(setting, 0.0f, 2.0f) * Mathf.Clamp(clearing * 1.5f, 0.0f, 1.0f) * calm * cool;
    }

    public override void _Process(double delta)
    {
        _accumulator += delta;
        if (_accumulator < UpdateSeconds) return;
        _accumulator = 0.0;
        Tick(Time.GetTicksMsec() * 0.001);
    }

    /// <summary>One evaluation at a monotonic clock (public so a test can drive it).</summary>
    public void Tick(double nowSeconds)
    {
        float wake = 0.0f;
        if (WakeFogProvider != null) wake = WakeFogProvider();
        else if (_megastorm != null && _explorer != null && _megastorm.Triggered) wake = _megastorm.QueryNow(_explorer.CanonicalPosition).Hazards.WakeFog;
        wake = float.IsFinite(wake) ? Mathf.Clamp(wake, 0.0f, 1.0f) : 0.0f;
        LastWakeFog = wake;

        float setting = RingStormArrivalSettings.Current.PostRainMist;
        float post = 0.0f;
        if (setting > 0.0f && _layer != null)
        {
            StormArrivalState state = _layer.ObserverStormArrival;
            float wind = _effects?.CurrentWindSnapshot.MeanWind.Length() ?? 0.0f;
            LastClearing = state.Clearing;
            LastCalm = 1.0f - StormArrivalMath.SmoothStep(3.0f, 8.0f, wind);
            LastCool = StormArrivalMath.SmoothStep(0.0f, -0.1f, state.CoolingOffset);
            post = Target(setting, state.Clearing, wind, state.CoolingOffset);
        }
        TargetMist = Mathf.Max(post, wake);
        // The wake fog keeps the faster slew while it is up and while its published mist is still releasing above the post-rain level.
        _wakeHeld = wake > 0.0f || (_wakeHeld && RingAtmosphereModel.WeatherMist > post);
        // Exactly the old early-out (post-rain mist off or no weather layer, and no wake): jump to 0. With the setting on, a post-rain
        // target of 0 still ramps down through the slew below, as before.
        if ((setting <= 0.0f || _layer == null) && wake <= 0.0f && !_wakeHeld)
        {
            RingAtmosphereModel.SetWeatherMist(0.0f, nowSeconds, immediate: true);
            return;
        }
        RingAtmosphereModel.SetWeatherMist(TargetMist, nowSeconds, maxStepsPerPublish: _wakeHeld ? WakeStepsPerPublish : RingAtmosphereModel.WeatherMistMaxStepsPerPublish);
    }

    public override void _ExitTree()
    {
        // Leave the haze as the user set it when the world goes away.
        RingAtmosphereModel.ClearWeatherMist();
    }
}
