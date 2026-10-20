using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Shadows;

/// <summary>
/// S6 screen-space contact shadows (docs/shadow-revamp-plan.md S6). Main-thread host: owns the Texture2DRD global ringworld_contact_shadow, appends the
/// RingContactShadows.gd compositor effect (PRE_OPAQUE, main view only) to the main WorldEnvironment's Compositor while the feature is on, and publishes every frame
///   ringworld_contact_shadow_params = (enabled, surface strength, grass strength, main-view far fingerprint),
///   ringworld_contact_shadow_key    = (internal width, internal height, 0, 0)   (the size the effect actually wrote; receivers compare VIEWPORT_SIZE with it).
/// Far viewports use their own world and compositor, so the effect never runs there; receivers also reject a projection whose far fingerprint (|z/w| of NDC z = 0 under INV_PROJECTION_MATRIX, which the effect measures on the same matrix; the engine projection is infinite-far reverse-Z, so Camera3D.Far itself is not recoverable) differs from params.w by more than 1%.
/// enabled = 0 when the toggle is off, the sun shadow is off or suspended (low sun, zero energy, underground), or more than one view renders.
/// </summary>
public partial class RingContactShadows : Node
{
    public static RingContactShadows? Instance { get; private set; }
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export] public NodePath GraphicsPath { get; set; } = new("../GraphicsQuality");

    private const string ParamsGlobal = "ringworld_contact_shadow_params", KeyGlobal = "ringworld_contact_shadow_key", TextureGlobal = "ringworld_contact_shadow";
    private DirectionalLight3D? _sun;
    private RingGraphicsQualityController? _graphics;
    private CompositorEffect? _effect;
    private Texture2Drd? _texture;
    private WorldEnvironment? _environment;
    private Vector4 _publishedParams = new(float.NaN, 0, 0, 0), _publishedKey = new(float.NaN, 0, 0, 0);
    private bool? _markersEnabled;
    private string _markerBegin = "", _markerEnd = "";
    private int _settingsRevision = -1;
    private RingContactShadowSettings _settings = new();
    private bool _rendering;
    private Rid _requestedImage;
    private long _boundImageId; // written by the render-thread bind callable

    /// <summary>Transient test hook: overrides the sun direction toward the light (world space) when non-zero; also lets tests run without a sun node.</summary>
    public Vector3 SunDirectionOverride { get; set; }
    /// <summary>Transient test hook: when set it replaces the settings resolved from the graphics controller.</summary>
    public RingContactShadowSettings? SettingsOverride { get; set; }
    public RingContactShadowSettings Settings => _settings;
    public CompositorEffect? Effect => _effect;
    public bool Active => _rendering;
    public Vector2I ReportedSize { get; private set; }
    public string StatusText { get; private set; } = "Contact shadows: off.";
    public Vector4 PublishedParams => _publishedParams;
    public Vector4 PublishedKey => _publishedKey;

    /// <summary>Tests: replaces the settings resolved from the graphics controller (the node then needs no controller).</summary>
    public void ConfigureForTest(bool enabled, float lengthMeters, float thicknessMeters, float strength, float fadeMeters, float grassStrength, int steps)
        => SettingsOverride = new RingContactShadowSettings { Enabled = enabled, LengthMeters = lengthMeters, ThicknessMeters = thicknessMeters, Strength = strength, FadeMeters = fadeMeters, GrassStrength = grassStrength, Steps = steps };

    public override void _EnterTree() => Instance = this;

    public override void _Ready()
    {
        _sun = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        _graphics = GetNodeOrNull<RingGraphicsQualityController>(GraphicsPath);
        EnsureTexture();
        ProcessPriority = 100; // after the camera and sun have moved this frame
        SetProcess(true);
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        PublishGlobals(new Vector4(0, 0, 0, 0), new Vector4(0, 0, 0, 0));
        DetachEffect();
        if (_effect != null) { _effect.Dispose(); _effect = null; }
        _texture = null;
    }

    private void EnsureTexture()
    {
        if (_texture != null) return;
        _texture = new Texture2Drd();
        RenderingServer.GlobalShaderParameterSet(TextureGlobal, _texture);
    }

    private bool EnsureEffect()
    {
        if (_effect != null) return true;
        if (RenderingServer.GetRenderingDevice() == null) return false;
        _effect = GD.Load<GDScript>("res://scripts/render/RingContactShadows.gd")?.New().AsGodotObject() as CompositorEffect;
        if (_effect == null) { GD.PushWarning("Contact shadow compositor effect unavailable."); return false; }
        EnsureTexture();
        _effect.Call("bind_texture", _texture!);
        return true;
    }

    private static WorldEnvironment? FindEnvironment(Viewport viewport)
    {
        foreach (Node candidate in viewport.FindChildren("*", "WorldEnvironment", true, false))
            if (candidate is WorldEnvironment owner && owner.GetViewport() == viewport) return owner;
        return null;
    }

    private void AttachEffect(Viewport viewport)
    {
        if (_effect == null) return;
        if (_environment != null && (!GodotObject.IsInstanceValid(_environment) || _environment.GetViewport() != viewport)) _environment = null;
        _environment ??= FindEnvironment(viewport);
        if (_environment == null) return;
        _environment.Compositor ??= new Compositor();
        var effects = _environment.Compositor.CompositorEffects;
        using Godot.Collections.Array lifetime = (Godot.Collections.Array)effects;
        if (effects.Contains(_effect)) return;
        effects.Add(_effect);
        _environment.Compositor.CompositorEffects = effects;
    }

    private void DetachEffect()
    {
        if (_environment != null && GodotObject.IsInstanceValid(_environment) && _environment.Compositor != null && _effect != null)
        {
            var effects = _environment.Compositor.CompositorEffects;
            using Godot.Collections.Array lifetime = (Godot.Collections.Array)effects;
            if (effects.Remove(_effect)) _environment.Compositor.CompositorEffects = effects;
        }
        _environment = null;
    }

    private static Vector2I TargetSize(Viewport viewport) => viewport switch
    {
        Window window => window.Size,
        SubViewport sub => sub.Size,
        _ => Vector2I.Zero,
    };

    public override void _Process(double delta)
    {
        _graphics ??= GetNodeOrNull<RingGraphicsQualityController>(GraphicsPath);
        _sun ??= GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        if (SettingsOverride != null) _settings = SettingsOverride.Normalized();
        else if (_graphics != null && _settingsRevision != _graphics.HeightShadowRevision)
        {
            _settings = _graphics.ResolveContactShadowSettings();
            _settingsRevision = _graphics.HeightShadowRevision;
        }
        Viewport? viewport = GetViewport();
        Camera3D? camera = viewport?.GetCamera3D();
        bool sunOverride = SunDirectionOverride.LengthSquared() > 0.5f;
        bool sunOk = sunOverride
            || (_sun != null && GodotObject.IsInstanceValid(_sun) && _sun.IsInsideTree() && _sun.ShadowEnabled && _sun.LightEnergy > RingGraphicsQualityController.SunShadowMinimumEnergy
                && (_graphics == null || (!_graphics.LocalSunShadowSuspended && !_graphics.UndergroundShadowSuspended)));
        bool wanted = _settings.Enabled && viewport != null && camera != null && !viewport.UseXR;
        if (!wanted)
        {
            Release("Contact shadows: off.");
            return;
        }
        if (!EnsureEffect()) { StatusText = "Contact shadows: unavailable (no RenderingDevice)."; return; }
        AttachEffect(viewport!);
        bool enabled = sunOk && _environment != null;
        Vector3 sunWorld = sunOverride ? SunDirectionOverride.Normalized() : _sun != null ? _sun.GlobalBasis.Z.Normalized() : Vector3.Up;
        Viewport.Scaling3DModeEnum mode = viewport!.Scaling3DMode;
        bool temporal = mode == Viewport.Scaling3DModeEnum.Fsr2 || viewport.UseTaa;
        _effect!.Call("publish_frame", enabled, sunWorld, _settings.LengthMeters, _settings.ThicknessMeters, _settings.Steps, _settings.FadeMeters, temporal,
            (int)(Engine.GetProcessFrames() & 0x7fffffff), TargetSize(viewport), camera!.GlobalTransform);
        bool diagnostics = RingworldTechDemo.Diagnostics.RingPerformanceGpu.Enabled;
        string begin = RingworldTechDemo.Diagnostics.RingPerformanceGpu.ContactShadowBeginMarker, end = RingworldTechDemo.Diagnostics.RingPerformanceGpu.ContactShadowEndMarker;
        if (_markersEnabled != diagnostics || _markerBegin != begin || _markerEnd != end)
        {
            _effect.Call("publish_gpu_markers", diagnostics, begin, end);
            _markersEnabled = diagnostics; _markerBegin = begin; _markerEnd = end;
        }
        Vector2I size = _effect.Call("get_reported_size").AsVector2I();
        ReportedSize = size;
        // Bind the effect's image to the global texture between frames (see get_image_rid); receivers stay off until the binding has run.
        Rid image = _effect.Call("get_image_rid").AsRid();
        if (image.IsValid && image != _requestedImage)
        {
            _requestedImage = image;
            Texture2Drd texture = _texture!;
            RenderingServer.CallOnRenderThread(Callable.From(() =>
            {
                texture.TextureRdRid = image;
                System.Threading.Volatile.Write(ref _boundImageId, (long)image.Id);
            }));
        }
        bool bound = image.IsValid && System.Threading.Volatile.Read(ref _boundImageId) == (long)image.Id;
        bool live = enabled && bound && size.X > 0 && size.Y > 0;
        _rendering = live;
        PublishGlobals(
            new Vector4(live ? 1f : 0f, _settings.Strength, _settings.GrassStrength, _effect.Call("get_derived_far").AsSingle()),
            live ? new Vector4(size.X, size.Y, 0, 0) : new Vector4(0, 0, 0, 0));
        StatusText = !enabled ? "Contact shadows: waiting (sun shadow off or suspended)." : live ? $"Contact shadows: on, {size.X}x{size.Y}, {_settings.Steps} steps." : "Contact shadows: starting.";
    }

    private void Release(string status)
    {
        _rendering = false;
        ReportedSize = Vector2I.Zero;
        StatusText = status;
        PublishGlobals(new Vector4(0, 0, 0, 0), new Vector4(0, 0, 0, 0));
        if (_effect != null && _environment != null)
        {
            _effect.Call("publish_frame", false, Vector3.Up, 1f, 1f, 8, 1f, false, 0, Vector2I.Zero, Transform3D.Identity);
            DetachEffect();
        }
    }

    private void PublishGlobals(Vector4 parameters, Vector4 key)
    {
        if (_publishedParams != parameters) { _publishedParams = parameters; RenderingServer.GlobalShaderParameterSet(ParamsGlobal, parameters); }
        if (_publishedKey != key) { _publishedKey = key; RenderingServer.GlobalShaderParameterSet(KeyGlobal, key); }
    }
}
