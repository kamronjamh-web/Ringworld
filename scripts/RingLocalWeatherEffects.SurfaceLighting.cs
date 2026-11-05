using Godot;
using RingworldTechDemo.Lightning;

namespace RingworldTechDemo;

public partial class RingLocalWeatherEffects
{
    public LightningSurfaceLighting SurfaceLightning { get; } = new();
    readonly OmniLight3D?[] _surfaceLights = new OmniLight3D?[LightningSurfaceLighting.MaximumLights];
    double _surfaceLightningTime = double.NegativeInfinity;
    ulong _surfaceLightningFrame = ulong.MaxValue;

    internal void UpdateSurfaceLightning()
    {
        // Either viewport may process first; both consume one selection for this rendered frame.
        ulong frame = Engine.GetProcessFrames();
        if (_surfaceLightningFrame == frame) return;
        _surfaceLightningFrame = frame;
        SurfaceLightning.Clear();
        if (_weatherSource != null && _grid != null && _explorer != null && Enabled && EnableLightning
            && _weatherSource.Enabled && _weatherSource.EnableSharedLightning && !UndergroundSuspended
            && UndergroundFlashSuppression < 0.999f && LightningVisualPolicy.SurfaceIlluminationStrength > 0.0f)
        {
            double now = _weatherSource.CloudSimulationTimeSeconds;
            if (now >= _surfaceLightningTime)
            {
                MegastormModel? model = _weatherSource.MegastormSource switch
                { RingMegastorm node => node.Model, MegastormModel source => source, _ => null };
                SurfaceLightning.Update(_weatherSource.ActiveLightningEvents, model, now, _explorer.CanonicalPosition,
                    _grid.GetCanonicalRingCircumferenceMeters64(), LightningColor, UndergroundFlashSuppression);
            }
            _surfaceLightningTime = now;
        }
        for (int i = 0; i < _surfaceLights.Length; i++)
        {
            if (i < SurfaceLightning.Count && _grid != null)
            {
                if (_surfaceLights[i] == null)
                {
                    _surfaceLights[i] = LightningSurfaceLighting.CreateLight($"SurfaceLightning{i}");
                    AddChild(_surfaceLights[i]!);
                    _surfaceLights[i]!.TopLevel = true;
                }
                var state = SurfaceLightning[i];
                var light = _surfaceLights[i]!;
                light.GlobalPosition = _grid.CaptureLodGeometrySnapshot().CanonicalToWorld(state.Center.AroundMeters, state.Center.VerticalMeters, state.Center.AcrossMeters);
                light.LightColor = state.Color;
                light.OmniRange = state.ReachMeters;
                light.LightEnergy = state.Energy;
                light.Visible = state.Energy > 0.0f;
            }
            else if (_surfaceLights[i] is { } light) { light.LightEnergy = 0.0f; light.Visible = false; }
        }
    }

    private void ReleaseSurfaceLightning()
    {
        SurfaceLightning.Clear();
        if (_farViewport != null && GodotObject.IsInstanceValid(_farViewport)) _farViewport.UnbindSurfaceLightning(this);
        foreach (var light in _surfaceLights)
            if (light != null && GodotObject.IsInstanceValid(light)) { light.LightEnergy = 0.0f; light.QueueFree(); }
    }
}
