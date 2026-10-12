using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Weather;
using RingworldTechDemo.Diagnostics;

namespace RingworldTechDemo;

/// <summary>Local liquid rain is a depth-aware veil. Environment fog remains independently owned.</summary>
public partial class RingLocalWeatherEffects
{
    [Export] public NodePath RainHazeCameraPath { get; set; } = new("../Explorer/Head/Camera3D");
    [Export] public string RainHazePreferencesPath { get; set; } = GameplayRainHazeDefaults.UserPath;
    private Camera3D? _rainHazeCamera;
    private MeshInstance3D? _rainHaze;
    private ShaderMaterial? _rainHazeMaterial;
    private RingDayNightCycle? _rainHazeDayNight;
    private RingWeatherAmbience? _rainHazeAmbience;
    private ulong _rainHazeOwner;
    private static ulong _activeRainHazeOwner;
    private static WeakReference<RingLocalWeatherEffects>? _activeRainHazeEffects;
    private readonly Dictionary<StringName, Variant> _rainHazeParameters = new();
    private long _rainHazeParameterPushes;
    private CompositorEffect? _rainHazeCompositor;
    private WorldEnvironment? _rainHazeWorldEnvironment;
    private Texture2D? _rainHazeNeutral;
    private bool _rainHazeCompositeActive;
    private bool? _rainHazeGpuMarkersEnabled;
    private string _rainHazeGpuBeginMarker = string.Empty, _rainHazeGpuEndMarker = string.Empty;

    private void CreateRainHaze()
    {
        // A replacement with the same preference path inherits live UI edits,
        // including unsaved overrides. A different world/fixture path starts
        // from authored defaults and its own persisted overrides.
        if (GameplayRainHazeDefaults.StartupDefaults?.GetPath() != RainHazePreferencesPath)
        {
            RingRainHazeSettings.Current = new RingRainHazeSettings();
            GameplayRainHazeDefaults.ApplyStartup(RainHazePreferencesPath);
        }
        _rainHazeCamera = GetNodeOrNull<Camera3D>(RainHazeCameraPath);
        _rainHazeAmbience = GetNodeOrNull<RingWeatherAmbience>("../WeatherAmbience");
        _rainHazeDayNight = GetNodeOrNull<RingDayNightCycle>("../RingDayNightCycle");
        if (_rainHazeCamera == null) return;
        Shader? shader = GD.Load<Shader>("res://materials/ring_rain_haze.gdshader");
        if (shader == null) return;
        if (_activeRainHazeEffects != null && _activeRainHazeEffects.TryGetTarget(out RingLocalWeatherEffects? previous)
            && GodotObject.IsInstanceValid(previous) && previous._rainHaze != null && GodotObject.IsInstanceValid(previous._rainHaze))
            previous.ReleaseRainHaze();
        _activeRainHazeEffects = new(this);
        _rainHazeOwner = GetInstanceId();
        _activeRainHazeOwner = _rainHazeOwner;
        _rainHazeMaterial = new ShaderMaterial { Shader = shader, RenderPriority = 120 };
        _rainHaze = new MeshInstance3D
        {
            Name = "RainHaze",
            Mesh = new QuadMesh { Size = new Vector2(2, 2) },
            MaterialOverride = _rainHazeMaterial,
            Position = new Vector3(0, 0, -1),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            IgnoreOcclusionCulling = true,
            ExtraCullMargin = 16,
            Visible = false
        };
        // The main camera's world owns the veil. Minimap and scaled-far worlds
        // retain their own precipitation/atmosphere presentation.
        _rainHazeCamera.AddChild(_rainHaze);
        RenderingServer.InstanceSetIgnoreCulling(_rainHaze.GetInstance(), true);
        _rainHazeNeutral = GD.Load<Texture2D>("res://materials/ring_wind_neutral.tres");
        if (RenderingServer.GetRenderingDevice() != null)
            _rainHazeCompositor = GD.Load<GDScript>("res://scripts/render/RingRainHazeComposite.gd")?.New().AsGodotObject() as CompositorEffect;
        RefreshRainHazeCompositorOwner();
    }

    private void UpdateRainHaze()
    {
        if (_rainHaze == null || !GodotObject.IsInstanceValid(_rainHaze) || _rainHazeMaterial == null) return;
        RingRainHazeSettings settings = RingRainHazeSettings.Current;
        bool eligible = _activeRainHazeOwner == _rainHazeOwner && Enabled && _weatherSource?.Enabled == true
            && settings.Enabled && !Snowing && _explorer?.IsCameraUnderwater != true
            && _rainHazeAmbience?.IsRainSheltered != true
            && _rainHazeCamera != null && GodotObject.IsInstanceValid(_rainHazeCamera) && _rainHazeCamera.Current;
        float intensity = float.IsFinite(PrecipitationIntensity) ? Mathf.Clamp(PrecipitationIntensity, 0, 1) : 0;
        float density = FiniteHaze(settings.Density, 0, .03f, .004f);
        float maximumOpacity = FiniteHaze(settings.MaximumOpacity, 0, .9f, .6f);
        RefreshRainHazeCompositorOwner();
        bool compositorReady = _rainHazeWorldEnvironment != null && _rainHazeCompositor?.Get("ready").AsBool() == true;
        bool active = eligible && intensity > .001f && density > 0 && maximumOpacity > 0;
        _rainHaze.Visible = active && !compositorReady;
        _rainHazeCompositeActive = compositorReady && active;
        PublishRainHazeFrame(_rainHazeCompositeActive, intensity, density, maximumOpacity, settings);
        if (!_rainHaze.Visible) return;
        Color tint = LinearRainHazeTint(settings.Tint);
        SetRainHazeParameter("rain_intensity", intensity);
        SetRainHazeParameter("haze_density", density);
        SetRainHazeParameter("near_clear_distance", FiniteHaze(settings.NearClearDistanceMeters, 0, 100, 8));
        SetRainHazeParameter("maximum_opacity", maximumOpacity);
        SetRainHazeParameter("variation_strength", FiniteHaze(settings.VariationStrength, 0, 1, .25f));
        SetRainHazeParameter("haze_tint", new Vector3(Mathf.Clamp(tint.R, 0, 1), Mathf.Clamp(tint.G, 0, 1), Mathf.Clamp(tint.B, 0, 1)));
        SetRainHazeParameter("daylight", FiniteHaze(_rainHazeDayNight?.DaylightFactor ?? 1, 0, 1, 1));
        SetRainHazeParameter("sky_distance", Mathf.Min(750, _rainHazeCamera!.Far));
    }

    private void SetRainHazeParameter(StringName name, Variant value)
    {
        if (_rainHazeParameters.TryGetValue(name, out Variant previous) && previous.Equals(value)) return;
        _rainHazeParameters[name] = value;
        _rainHazeMaterial!.SetShaderParameter(name, value);
        _rainHazeParameterPushes++;
    }

    private static float FiniteHaze(float value, float minimum, float maximum, float fallback)
        => float.IsFinite(value) ? Mathf.Clamp(value, minimum, maximum) : fallback;

    // Preferences and the picker store sRGB. Both render paths receive explicit
    // linear RGB, independent of Variant type or shader source_color conversion.
    private static Color LinearRainHazeTint(Color tint)
    {
        if (!float.IsFinite(tint.R) || !float.IsFinite(tint.G) || !float.IsFinite(tint.B)) tint = new(.48f, .53f, .58f);
        return new Color(Math.Clamp(tint.R, 0, 1), Math.Clamp(tint.G, 0, 1), Math.Clamp(tint.B, 0, 1)).SrgbToLinear();
    }

    public static float EvaluateRainHazeOpacity(float intensity, float distance)
    {
        var settings = RingRainHazeSettings.Current;
        float rain = FiniteHaze(intensity, 0, 1, 0);
        float path = Mathf.Max(0, distance - FiniteHaze(settings.NearClearDistanceMeters, 0, 100, 8));
        return Mathf.Min(1f - Mathf.Exp(-FiniteHaze(settings.Density, 0, .03f, .004f) * rain * path),
            FiniteHaze(settings.MaximumOpacity, 0, .9f, .6f) * rain);
    }

    private void ReleaseRainHaze()
    {
        _rainHazeCompositeActive = false;
        if (_rainHazeCompositor != null)
        {
            Variant neutralTexture = _rainHazeNeutral == null ? default : Variant.From(_rainHazeNeutral);
            _rainHazeCompositor.Call("publish_frame", new byte[240], neutralTexture, false);
            DetachRainHazeCompositor();
            _rainHazeCompositor.Dispose(); _rainHazeCompositor = null;
            _rainHazeGpuMarkersEnabled = null;
        }
        if (_activeRainHazeOwner == _rainHazeOwner) { _activeRainHazeOwner = 0; _activeRainHazeEffects = null; }
        if (_rainHaze != null && GodotObject.IsInstanceValid(_rainHaze)) { _rainHaze.Visible = false; _rainHaze.QueueFree(); }
        _rainHaze = null;
        _rainHazeMaterial = null;
    }

    private void RefreshRainHazeCompositorOwner()
    {
        if (_rainHazeCompositor == null || _rainHazeCamera == null || !GodotObject.IsInstanceValid(_rainHazeCamera)) return;
        Viewport viewport = _rainHazeCamera.GetViewport();
        if (_rainHazeWorldEnvironment != null && (!GodotObject.IsInstanceValid(_rainHazeWorldEnvironment) || _rainHazeWorldEnvironment.GetViewport() != viewport)) DetachRainHazeCompositor();
        if (_rainHazeWorldEnvironment == null)
        {
            foreach (Node candidate in viewport.FindChildren("*", "WorldEnvironment", true, false))
                if (candidate is WorldEnvironment owner && owner.GetViewport() == viewport)
                { _rainHazeWorldEnvironment = owner; break; }
        }
        EnsureRainHazeCompositorLast(_rainHazeWorldEnvironment);
    }
    public static void EnsureRainHazeCompositorLast(WorldEnvironment? environment)
    {
        if (environment == null || !GodotObject.IsInstanceValid(environment) || _activeRainHazeEffects == null
            || !_activeRainHazeEffects.TryGetTarget(out var owner) || !GodotObject.IsInstanceValid(owner)
            || owner._rainHazeWorldEnvironment != environment || owner._rainHazeCompositor == null) return;
        environment.Compositor ??= new Compositor();
        var effects = environment.Compositor.CompositorEffects;
        using Godot.Collections.Array effectsLifetime = (Godot.Collections.Array)effects;
        if (effects.Count > 0 && effects[^1] == owner._rainHazeCompositor) return;
        effects.Remove(owner._rainHazeCompositor); effects.Add(owner._rainHazeCompositor);
        environment.Compositor.CompositorEffects = effects;
    }
    private void DetachRainHazeCompositor()
    {
        if (_rainHazeWorldEnvironment != null && GodotObject.IsInstanceValid(_rainHazeWorldEnvironment) && _rainHazeWorldEnvironment.Compositor != null)
        {
            var effects = _rainHazeWorldEnvironment.Compositor.CompositorEffects;
            using Godot.Collections.Array effectsLifetime = (Godot.Collections.Array)effects;
            if (_rainHazeCompositor != null && effects.Remove(_rainHazeCompositor)) _rainHazeWorldEnvironment.Compositor.CompositorEffects = effects;
        }
        _rainHazeWorldEnvironment = null;
    }
    private void PublishRainHazeFrame(bool active, float intensity, float density, float maximumOpacity, RingRainHazeSettings settings)
    {
        if (_rainHazeCompositor == null || _rainHazeCamera == null) return;
        bool diagnosticsEnabled = RingPerformanceGpu.Enabled;
        string beginMarker = RingPerformanceGpu.RainHazeBeginMarker, endMarker = RingPerformanceGpu.RainHazeEndMarker;
        if (_rainHazeGpuMarkersEnabled != diagnosticsEnabled || _rainHazeGpuBeginMarker != beginMarker || _rainHazeGpuEndMarker != endMarker)
        {
            _rainHazeCompositor.Call("publish_gpu_markers", diagnosticsEnabled, beginMarker, endMarker);
            _rainHazeGpuMarkersEnabled = diagnosticsEnabled;
            _rainHazeGpuBeginMarker = beginMarker; _rainHazeGpuEndMarker = endMarker;
        }
        var wind = _weatherSource?.SharedWindField.CapturePublishedFrame() ?? default;
        // A fresh immutable payload is published atomically; callbacks never read live nodes or globals.
        byte[] data = new byte[240]; int offset = 64;
        void Vector(Vector4 value)
        {
            BitConverter.TryWriteBytes(data.AsSpan(offset,4),value.X); offset += 4;
            BitConverter.TryWriteBytes(data.AsSpan(offset,4),value.Y); offset += 4;
            BitConverter.TryWriteBytes(data.AsSpan(offset,4),value.Z); offset += 4;
            BitConverter.TryWriteBytes(data.AsSpan(offset,4),value.W); offset += 4;
        }
        Transform3D camera = _rainHazeCamera.GetCameraTransform();
        Vector(new(camera.Basis.X.X, camera.Basis.X.Y, camera.Basis.X.Z, 0));
        Vector(new(camera.Basis.Y.X, camera.Basis.Y.Y, camera.Basis.Y.Z, 0));
        Vector(new(camera.Basis.Z.X, camera.Basis.Z.Y, camera.Basis.Z.Z, 0));
        Vector(new(camera.Origin.X, camera.Origin.Y, camera.Origin.Z, 1));
        Color tint = LinearRainHazeTint(settings.Tint);
        Vector(new(intensity, density, FiniteHaze(settings.NearClearDistanceMeters,0,100,8), maximumOpacity));
        Vector(new(Math.Clamp(tint.R,0,1),Math.Clamp(tint.G,0,1),Math.Clamp(tint.B,0,1),FiniteHaze(_rainHazeDayNight?.DaylightFactor ?? 1,0,1,1)));
        Vector(new(FiniteHaze(settings.VariationStrength,0,1,.25f),Mathf.Min(750,_rainHazeCamera.Far),wind.Enabled ? 1 : 0,0));
        Vector(new(wind.Anchor.X,wind.Anchor.Y,wind.Anchor.Z,0)); Vector(wind.Around); Vector(wind.Across);
        Vector(new(wind.Uv.X,wind.Uv.Y,wind.Base,wind.Strength));
        Texture2D? texture = wind.Texture ?? _rainHazeNeutral;
        Variant frameTexture = texture == null ? default : Variant.From(texture);
        _rainHazeCompositor.Call("publish_frame", data, frameTexture, active);
    }

    public Godot.Collections.Dictionary GetRainHazeDiagnostics() => new()
    {
        ["visible"] = _rainHazeCompositeActive || (_rainHaze != null && GodotObject.IsInstanceValid(_rainHaze) && _rainHaze.Visible),
        ["fallback_visible"] = _rainHaze != null && GodotObject.IsInstanceValid(_rainHaze) && _rainHaze.Visible,
        ["compositor_active"] = _rainHazeCompositeActive,
        ["compositor_ready"] = _rainHazeCompositor?.Get("ready").AsBool() ?? false,
        ["compositor_id"] = _rainHazeCompositor != null ? (long)_rainHazeCompositor.GetInstanceId() : 0,
        ["compositor_dispatches"] = _rainHazeCompositor?.Get("dispatches").AsInt64() ?? 0,
        ["compositor_timestamp_scopes"] = _rainHazeCompositor?.Get("timestamp_scopes").AsInt64() ?? 0,
        ["rain_intensity"] = PrecipitationIntensity,
        ["parameter_pushes"] = _rainHazeParameterPushes,
        ["snow"] = Snowing,
        ["underwater"] = _explorer?.IsCameraUnderwater == true,
        ["sheltered"] = _rainHazeAmbience?.IsRainSheltered == true,
        ["active_owner"] = _activeRainHazeOwner == _rainHazeOwner && _rainHazeOwner != 0,
        ["density"] = FiniteHaze(RingRainHazeSettings.Current.Density, 0, .03f, .004f),
        ["maximum_opacity"] = FiniteHaze(RingRainHazeSettings.Current.MaximumOpacity, 0, .9f, .6f),
        ["opacity_100m"] = EvaluateRainHazeOpacity(PrecipitationIntensity, 100f),
        ["camera_id"] = _rainHazeCamera != null && GodotObject.IsInstanceValid(_rainHazeCamera) ? (long)_rainHazeCamera.GetInstanceId() : 0,
        ["mesh_id"] = _rainHaze != null && GodotObject.IsInstanceValid(_rainHaze) ? (long)_rainHaze.GetInstanceId() : 0
    };
}
