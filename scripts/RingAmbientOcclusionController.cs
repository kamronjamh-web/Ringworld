using System;
using Godot;

namespace RingworldTechDemo;

/// <summary>
/// Configures a Minecraft-strength screen-space ambient occlusion approximation
/// for nearby voxel corners and terrain contacts, with an A/B key and measured
/// GPU-frame reporting.
/// </summary>
public partial class RingAmbientOcclusionController : Node
{
    [Export] public bool Enabled { get; set; } = true;
    [Export] public NodePath WorldEnvironmentPath { get; set; } = new("../WorldEnvironment");
    // One local voxel is one meter. Keeping the radius close to one voxel makes
    // this read as block-corner shading instead of broad terrain dirt.
    [Export(PropertyHint.Range, "0.1,8.0,0.1")] public float RadiusMeters { get; set; } = 1.2f;
    [Export(PropertyHint.Range, "0.0,4.0,0.05")] public float Intensity { get; set; } = 2.15f;
    [Export(PropertyHint.Range, "0.1,4.0,0.05")] public float Power { get; set; } = 1.45f;
    [Export(PropertyHint.Range, "0.0,1.0,0.05")] public float Detail { get; set; } = 0.80f;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float Horizon { get; set; } = 0.08f;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float Sharpness { get; set; } = 0.98f;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float DirectLightAffect { get; set; } = 0.38f;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float MaterialAoChannelAffect { get; set; } = 0.0f;

    [ExportGroup("Diagnostics")]
    [Export] public Key ToggleKey { get; set; } = Key.J;
    [Export] public bool MeasureGpuFrameTime { get; set; } = false;
    [Export(PropertyHint.Range, "1,10,0.5")] public float TimingReportSeconds { get; set; } = 3.0f;

    private WorldEnvironment? _worldEnvironment;
    private Rid _viewportRid;
    private bool _runtimeEnabled;
    private double _timingElapsed;
    private double _gpuFrameTotal;
    private int _gpuFrameSamples;

    public override void _Ready()
    {
        _worldEnvironment = GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath);
        if (_worldEnvironment?.Environment == null)
        {
            GD.PushWarning("Ring ambient occlusion disabled: WorldEnvironment is missing.");
            return;
        }

        ApplyParameters();
        SetAmbientOcclusionEnabled(Enabled);
        _viewportRid = GetViewport().GetViewportRid();
        if (MeasureGpuFrameTime)
            RenderingServer.ViewportSetMeasureRenderTime(_viewportRid, true);
        else
            SetProcess(false);
        GD.Print($"Ring SSAO ready (Minecraft-strength): radius={RadiusMeters:0.0}m, intensity={Intensity:0.00}, directLight={DirectLightAffect:0.00}, A/B={ToggleKey}.");
    }

    public override void _Process(double delta)
    {
        if (!MeasureGpuFrameTime || !_viewportRid.IsValid) return;
        double gpuMilliseconds = RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewportRid);
        if (gpuMilliseconds > 0.0 && double.IsFinite(gpuMilliseconds))
        {
            _gpuFrameTotal += gpuMilliseconds;
            _gpuFrameSamples++;
        }
        _timingElapsed += delta;
        if (_timingElapsed < Math.Max(1.0, TimingReportSeconds)) return;
        if (_gpuFrameSamples > 0)
        {
            GD.Print($"SSAO A/B GPU [{(_runtimeEnabled ? "ON" : "OFF")}]: total frame {_gpuFrameTotal / _gpuFrameSamples:0.00} ms average over {_gpuFrameSamples} samples.");
        }
        ResetTiming();
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey keyEvent
            || !keyEvent.Pressed
            || keyEvent.Echo
            || keyEvent.Keycode != ToggleKey)
            return;
        SetAmbientOcclusionEnabled(!_runtimeEnabled);
        GetViewport().SetInputAsHandled();
        GD.Print($"SSAO A/B: {(_runtimeEnabled ? "ON" : "OFF")} (key {ToggleKey}).");
    }

    public void SetAmbientOcclusionEnabled(bool enabled)
    {
        _runtimeEnabled = enabled && _worldEnvironment?.Environment != null;
        if (_worldEnvironment?.Environment is { } environment)
            environment.SsaoEnabled = _runtimeEnabled;
        ResetTiming();
    }

    private void ApplyParameters()
    {
        if (_worldEnvironment?.Environment is not { } environment) return;
        environment.SsaoRadius = Mathf.Max(0.1f, RadiusMeters);
        environment.SsaoIntensity = Mathf.Max(0.0f, Intensity);
        environment.SsaoPower = Mathf.Max(0.1f, Power);
        environment.SsaoDetail = Mathf.Clamp(Detail, 0.0f, 1.0f);
        environment.SsaoHorizon = Mathf.Clamp(Horizon, 0.0f, 1.0f);
        environment.SsaoSharpness = Mathf.Clamp(Sharpness, 0.0f, 1.0f);
        environment.SsaoLightAffect = Mathf.Clamp(DirectLightAffect, 0.0f, 1.0f);
        environment.SsaoAOChannelAffect = Mathf.Clamp(MaterialAoChannelAffect, 0.0f, 1.0f);
    }

    private void ResetTiming()
    {
        _timingElapsed = 0.0;
        _gpuFrameTotal = 0.0;
        _gpuFrameSamples = 0;
    }
}
