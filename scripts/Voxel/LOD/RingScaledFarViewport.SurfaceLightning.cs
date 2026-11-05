using Godot;
using RingworldTechDemo.Lightning;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingScaledFarViewport
{
    RingLocalWeatherEffects? _surfaceLightningSource;
    readonly OmniLight3D?[] _farSurfaceLights = new OmniLight3D?[LightningSurfaceLighting.MaximumLights];
    readonly OmniLight3D?[] _regionalSurfaceLights = new OmniLight3D?[LightningSurfaceLighting.MaximumLights];

    public void BindSurfaceLightning(RingLocalWeatherEffects source) => _surfaceLightningSource = source;
    public void UnbindSurfaceLightning(RingLocalWeatherEffects source)
    {
        if (_surfaceLightningSource != source) return;
        _surfaceLightningSource = null;
        ClearSurfaceLightningMirrors();
    }

    private void UpdateSurfaceLightningMirrors()
    {
        if (_surfaceLightningSource != null && GodotObject.IsInstanceValid(_surfaceLightningSource))
            _surfaceLightningSource.UpdateSurfaceLightning();
        LightningSurfaceLighting? state = _surfaceLightningSource != null && GodotObject.IsInstanceValid(_surfaceLightningSource)
            ? _surfaceLightningSource.SurfaceLightning : null;
        UpdateSurfaceLightningWorld(_farViewport, _farSurfaceLights, state, false);
        UpdateSurfaceLightningWorld(_regionalFarViewport, _regionalSurfaceLights, state, true);
    }

    private void UpdateSurfaceLightningWorld(SubViewport? viewport, OmniLight3D?[] lights, LightningSurfaceLighting? state, bool regional)
    {
        int count = Enabled && !RenderSuspended && viewport != null && viewport.RenderTargetUpdateMode != SubViewport.UpdateMode.Disabled
            && (!regional || RegionalFarLodsEnabled) ? state?.Count ?? 0 : 0;
        for (int i = 0; i < lights.Length; i++)
        {
            if (i < count)
            {
                if (lights[i] == null)
                {
                    lights[i] = LightningSurfaceLighting.CreateLight($"SurfaceLightning{i}");
                    viewport!.AddChild(lights[i]!);
                }
                var selected = state![i];
                var center = selected.Center;
                var light = lights[i]!;
                light.Position = regional ? ScaledPointRelativeForBuild(center.AroundMeters, center.VerticalMeters, center.AcrossMeters,
                    _regionalRenderOriginAround, 0.0, _regionalRenderOriginAcross, _circumferenceMeters, _radiusMeters, Mathf.Max(1, RingWidthMeters), CoordinateScale)
                    : ScaledPoint(center.AroundMeters, center.VerticalMeters, center.AcrossMeters);
                light.OmniRange = selected.ReachMeters * CoordinateScale;
                light.LightColor = selected.Color;
                light.LightEnergy = selected.Energy;
                light.Visible = selected.Energy > 0.0f;
            }
            else if (lights[i] is { } light) { light.LightEnergy = 0.0f; light.Visible = false; }
        }
    }

    private void ClearSurfaceLightningMirrors()
    {
        foreach (var light in _farSurfaceLights)
            if (light != null && GodotObject.IsInstanceValid(light)) { light.LightEnergy = 0.0f; light.Visible = false; }
        foreach (var light in _regionalSurfaceLights)
            if (light != null && GodotObject.IsInstanceValid(light)) { light.LightEnergy = 0.0f; light.Visible = false; }
    }
}
