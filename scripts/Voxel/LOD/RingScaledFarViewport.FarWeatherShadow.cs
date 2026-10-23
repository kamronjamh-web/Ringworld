using System;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingScaledFarViewport
{
    // Whole-ring bake of the far cloud shell's cloud amount for the far weather cloud shadow
    // (materials/ring_far_weather_shadow.gdshaderinc). Evaluating the shell's Sunshine-noise density per
    // receiver pixel cost about 1.2 ms at altitude (RTX 4070, 1080p); one fetch from this map costs
    // about 0.15 ms there, and a rebake evaluates each texel once. The texel matches the Sunshine far
    // shadow cascade's (~1.6 km over 200 km), so the 100 km handoff keeps the same level of detail.
    private const float FarWeatherCloudBakeTexelMeters = 2000.0f;
    // Weather drifts a few tens of metres between bakes; the atlas itself refreshes every few seconds.
    private const double FarWeatherCloudBakeIntervalSeconds = 2.0;
    // A full bake costs about 1.2 ms on an RTX 4070 (15708 x 159 texels, 2 x 2 samples). It is drawn as
    // vertical strips on consecutive frames into a never-cleared target, about 0.15 ms per frame.
    private const int FarWeatherCloudBakeStrips = 8;
    private readonly ColorRect?[] _farWeatherCloudBakeStrips = new ColorRect?[FarWeatherCloudBakeStrips];
    private int _farWeatherCloudBakeNextStrip = -1;
    private SubViewport? _farWeatherCloudBakeViewport;
    private ShaderMaterial? _farWeatherCloudBakeMaterial;
    private double _farWeatherCloudBakeElapsed;
    private bool _farWeatherCloudBakeRequested;
    private int _farWeatherCloudBakePublishCountdown = -1;
    public ulong FarWeatherCloudBakeCount { get; private set; }

    private void CreateFarWeatherCloudBake()
    {
        if (_farWeatherCloudBakeViewport != null) return;
        Shader? shader = GD.Load<Shader>("res://materials/ring_far_weather_cloud_bake.gdshader");
        if (shader == null || _circumferenceMeters <= 1.0) return;
        var size = new Vector2I(
            Math.Clamp((int)Math.Ceiling(_circumferenceMeters / FarWeatherCloudBakeTexelMeters), 64, 16384),
            Math.Clamp((int)Math.Ceiling(RingWidthMeters / FarWeatherCloudBakeTexelMeters), 8, 1024));
        _farWeatherCloudBakeMaterial = new ShaderMaterial { Shader = shader };
        _farWeatherCloudBakeMaterial.SetShaderParameter("ring_circumference_km", (float)(_circumferenceMeters * 0.001));
        _farWeatherCloudBakeMaterial.SetShaderParameter("ring_width_km", RingWidthMeters * 0.001f);
        _farWeatherCloudBakeMaterial.SetShaderParameter("coverage", FarCloudCoverage);
        _farWeatherCloudBakeMaterial.SetShaderParameter("softness", FarCloudSoftness);
        _farWeatherCloudBakeViewport = new SubViewport
        {
            Name = "FarWeatherCloudBake",
            Size = size,
            Disable3D = true,
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            RenderTargetClearMode = SubViewport.ClearMode.Never,
        };
        for (int strip = 0; strip < FarWeatherCloudBakeStrips; strip++)
        {
            int x0 = size.X * strip / FarWeatherCloudBakeStrips, x1 = size.X * (strip + 1) / FarWeatherCloudBakeStrips;
            var rect = new ColorRect
            {
                Material = _farWeatherCloudBakeMaterial,
                Position = new Vector2(x0, 0),
                Size = new Vector2(x1 - x0, size.Y),
                Visible = false,
            };
            _farWeatherCloudBakeStrips[strip] = rect;
            _farWeatherCloudBakeViewport.AddChild(rect);
        }
        AddChild(_farWeatherCloudBakeViewport);
        // The Sunshine density resources reach this material through ConfigureSunshineFarCloudDetail.
        _hasFarCloudDetailStatic = false;
        _hasFarCloudDetailPositions = false;
        if (_farWeatherAtlas != null) ConfigureFarWeatherMaterial(_farWeatherCloudBakeMaterial);
        _farWeatherCloudBakeRequested = true;
    }

    private void RequestFarWeatherCloudBake() => _farWeatherCloudBakeRequested = true;

    private void UpdateFarWeatherCloudBake(double delta)
    {
        if (_farWeatherCloudBakeViewport == null || _farWeatherCloudBakeMaterial == null) return;
        if (_farWeatherCloudBakePublishCountdown > 0 && --_farWeatherCloudBakePublishCountdown == 0)
        {
            global::RingworldTechDemo.RingFarWeatherShadowGlobals.PublishCloudMap(
                _farWeatherCloudBakeViewport.GetTexture(), _circumferenceMeters, RingWidthMeters);
        }
        if (_farWeatherCloudBakeNextStrip >= 0)
        {
            DrawFarWeatherCloudBakeStrip(_farWeatherCloudBakeNextStrip);
            return;
        }
        if (_farWeatherAtlas == null) return;
        _farWeatherCloudBakeElapsed += Math.Max(0.0, delta);
        if (!_farWeatherCloudBakeRequested && _farWeatherCloudBakeElapsed < FarWeatherCloudBakeIntervalSeconds) return;
        _farWeatherCloudBakeRequested = false;
        _farWeatherCloudBakeElapsed = 0.0;
        // Every strip of one bake uses the same weather clock.
        _farWeatherCloudBakeMaterial.SetShaderParameter("weather_time_seconds", ResolveFarWeatherAtlasAgeSeconds());
        DrawFarWeatherCloudBakeStrip(0);
    }

    private void DrawFarWeatherCloudBakeStrip(int strip)
    {
        for (int index = 0; index < FarWeatherCloudBakeStrips; index++)
            if (_farWeatherCloudBakeStrips[index] is { } rect) rect.Visible = index == strip;
        _farWeatherCloudBakeViewport!.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        _farWeatherCloudBakeNextStrip = strip + 1 < FarWeatherCloudBakeStrips ? strip + 1 : -1;
        if (_farWeatherCloudBakeNextStrip >= 0) return;
        FarWeatherCloudBakeCount++;
        // Receivers read the map once its first complete bake has been drawn.
        if (FarWeatherCloudBakeCount == 1) _farWeatherCloudBakePublishCountdown = 2;
    }
}
