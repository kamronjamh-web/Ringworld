using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Weather;

/// <summary>
/// Drives the post-rain mist of the physical haze (docs/storm-revamp-s3-storm-arrival-design.md section 5, WO-E1): M = PostRainMist x Clearing x calm x cool,
/// with calm = 1 - smoothstep(3, 8 m/s, wind) and cool = smoothstep(0, -0.1, CoolingOffset). The strength is published to RingAtmosphereModel.SetWeatherMist, which
/// quantises and rate-limits it. No GPU work of its own; at 0 the haze is exactly unchanged. Evaluated at 4 Hz.
/// </summary>
public partial class RingPostRainMist : Node
{
    [Export] public NodePath WeatherLayerPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath WeatherEffectsPath { get; set; } = new("../LocalWeatherEffects");
    [Export] public bool AutoBind { get; set; } = true;
    [Export(PropertyHint.Range, "0.05,2,0.05")] public float UpdateSeconds { get; set; } = 0.25f;

    private RingVolumetricCloudLayer? _layer;
    private RingLocalWeatherEffects? _effects;
    private double _accumulator;
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
        float setting = RingStormArrivalSettings.Current.PostRainMist;
        if (setting <= 0.0f || _layer == null)
        {
            TargetMist = 0.0f;
            RingAtmosphereModel.SetWeatherMist(0.0f, Time.GetTicksMsec() * 0.001);
            return;
        }
        StormArrivalState state = _layer.ObserverStormArrival;
        float wind = _effects?.CurrentWindSnapshot.MeanWind.Length() ?? 0.0f;
        LastClearing = state.Clearing;
        LastCalm = 1.0f - StormArrivalMath.SmoothStep(3.0f, 8.0f, wind);
        LastCool = StormArrivalMath.SmoothStep(0.0f, -0.1f, state.CoolingOffset);
        TargetMist = Target(setting, state.Clearing, wind, state.CoolingOffset);
        RingAtmosphereModel.SetWeatherMist(TargetMist, Time.GetTicksMsec() * 0.001);
    }

    public override void _ExitTree()
    {
        // Leave the haze as the user set it when the world goes away.
        RingAtmosphereModel.ClearWeatherMist();
    }
}
