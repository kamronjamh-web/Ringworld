using System;
using Godot;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo.Weather;

/// <summary>
/// Rain on the camera lens (docs/storm-revamp-s2-wet-world-design.md 3). A CanvasLayer 0 ColorRect with materials/ring_lens_droplets.gdshader, drawn after the whole 3D
/// frame (Sunshine, rain streaks, rain haze, FSR / TAA) and before the HUD layers, exactly like the underwater view. It stays out of the compositor chain, so the
/// haze-last invariant owned by the rain streak code is untouched. The wetness L comes from <see cref="LensWetnessModel"/> (rain at the camera x its shelter, look-up gain,
/// emergence burst, tau 5 s decay, zero underwater and in snow), advanced on the weather sim clock so a paused clock gives identical frames. The rect is hidden at L = 0 (no draw).
/// </summary>
public partial class RingLensDroplets : CanvasLayer
{
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath WeatherPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath EffectsPath { get; set; } = new("../LocalWeatherEffects");
    [Export] public NodePath WetWorldPath { get; set; } = new("../WetWorld");
    private const string ShaderPath = "res://materials/ring_lens_droplets.gdshader";
    private const double ClockPeriodSeconds = 512.0;

    public static RingLensDroplets? Instance { get; private set; }
    /// <summary>Test hook: replaces the modelled wetness (negative = use the model). The menu amount still applies.</summary>
    public float LensWetnessOverride { get; set; } = -1.0f;
    /// <summary>Camera override for tests and captures (default: the main viewport's current camera).</summary>
    public Camera3D? CameraSource { get; set; }
    public LensWetnessModel Model { get; } = new();
    public float Wetness { get; private set; }
    /// <summary>True while the full-screen rect is drawn (L x menu amount above the threshold).</summary>
    public bool Drawing => _rect != null && _rect.Visible;

    private ColorRect? _rect;
    private ShaderMaterial? _material;
    private RingVoxelExplorerController? _explorer;
    private RingVolumetricCloudLayer? _weather;
    private RingLocalWeatherEffects? _effects;
    private RingWetWorld? _wetWorld;
    private double _lastSimTime = double.NaN;
    private float _wetnessApplied = float.NaN, _amountApplied = float.NaN, _clockApplied = float.NaN, _aspectApplied = float.NaN;

    public override void _Ready()
    {
        Layer = 0;   // processed after the 3D view and before the gameplay HUD CanvasLayers
        Instance = this;
        Shader? shader = GD.Load<Shader>(ShaderPath);
        if (shader == null) { GD.PushWarning("Lens droplet shader could not be loaded: " + ShaderPath); return; }
        _material = new ShaderMaterial { Shader = shader };
        _rect = new ColorRect { Name = "LensDropletsView", MouseFilter = Control.MouseFilterEnum.Ignore, Material = _material, Visible = false };
        _rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_rect);
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(Instance, this)) Instance = null;
    }

    private void Resolve()
    {
        _explorer ??= GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _weather ??= GetNodeOrNull<RingVolumetricCloudLayer>(WeatherPath);
        _effects ??= GetNodeOrNull<RingLocalWeatherEffects>(EffectsPath);
        _wetWorld ??= GetNodeOrNull<RingWetWorld>(WetWorldPath);
    }

    public override void _Process(double delta)
    {
        if (_rect == null || _material == null) return;
        Resolve();
        float amount = Mathf.Clamp(RingWetWorldSettings.Current.LensDroplets, 0.0f, 1.0f);
        double simTime = _weather?.CloudSimulationTimeSeconds ?? 0.0;
        double simDelta = double.IsNaN(_lastSimTime) ? 0.0 : simTime - _lastSimTime;
        _lastSimTime = simTime;
        if (simDelta < 0.0 || simDelta > 5.0) simDelta = 0.0;   // a rewind or a jump is not weather time passing

        float wetness;
        if (LensWetnessOverride >= 0.0f) wetness = Mathf.Clamp(LensWetnessOverride, 0.0f, 1.0f);
        else
        {
            Camera3D? camera = CameraSource != null && IsInstanceValid(CameraSource) ? CameraSource : GetViewport()?.GetCamera3D();
            float lookUp = 0.0f;
            if (camera != null && IsInstanceValid(camera) && _wetWorld != null) lookUp = (-camera.GlobalBasis.Z).Dot(_wetWorld.CameraUpWorld);
            float rain = _effects != null && float.IsFinite(_effects.PrecipitationIntensity) ? _effects.PrecipitationIntensity : 0.0f;
            float exposure = _wetWorld?.CameraExposure ?? 1.0f;
            bool blocked = (_explorer?.IsCameraUnderwater ?? false) || (_effects?.Snowing ?? false);
            wetness = Model.Step((float)simDelta, rain, exposure, lookUp, blocked);
        }
        Wetness = wetness;
        bool draw = amount > 0.0f && wetness * amount > 0.002f;
        if (_rect.Visible != draw) _rect.Visible = draw;
        if (!draw) return;
        float clock = (float)WetMapFrame.Mod(simTime, ClockPeriodSeconds);
        Vector2 size = _rect.Size;
        float aspect = size.Y > 1.0f ? size.X / size.Y : 1.7778f;
        if (wetness != _wetnessApplied) { _wetnessApplied = wetness; _material.SetShaderParameter("lens_wetness", wetness); }
        if (amount != _amountApplied) { _amountApplied = amount; _material.SetShaderParameter("lens_amount", amount); }
        if (clock != _clockApplied) { _clockApplied = clock; _material.SetShaderParameter("clock_seconds", clock); }
        if (aspect != _aspectApplied) { _aspectApplied = aspect; _material.SetShaderParameter("screen_aspect", aspect); }
    }
}
