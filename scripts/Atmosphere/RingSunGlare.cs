using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Atmosphere;

/// <summary>
/// Sun veiling glare owner (LF-1; docs/sun-flare-shafts-near-fog-plan.md section A). Publishes one frame of CPU scalars to the compositor effect scripts/render/RingSunGlareComposite.gd
/// (two compute passes, ordered LAST by RingLocalWeatherEffects.AttachLensEffect): the sun direction the sky draws, the disc radius, the sun radiance fallback, the eclipse coverage, the storm
/// hide (CPU twin of the sky's sun_storm_hide), the enclosure gate (underwater / cave fog), the camera-column cloud transmittance, the strength and the draw window. The GPU measures the
/// visible fraction F and the displayed disc radiance itself. Strength 0, no sun or a window entirely off-screen publish inactive: the effect returns before any GPU call, and at strength 0 it
/// is removed from the compositor (a byte-identical frame). The strength is the "Sun glare" menu row (AtmosphereVisualDefaults.Apply -> <see cref="SetSettings"/>); "Lens flare" (ghosts, starburst)
/// is persisted and applied here but draws nothing until LF-2.
/// </summary>
public partial class RingSunGlare : Node
{
    [Export] public NodePath DaySidePath { get; set; } = new("../DaySideAtmosphere");
    [Export] public NodePath BridgePath { get; set; } = new("../SunshineCloudBridge");
    [Export] public NodePath DayNightPath { get; set; } = new("../RingDayNightCycle");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export] public NodePath WorldEnvironmentPath { get; set; } = new("../WorldEnvironment");

    /// <summary>CIE 1999 general disability glare function constants: age 25, pigmentation 0.5 (design constants, not menu rows).</summary>
    public const float Age = 25.0f, Pigmentation = 0.5f;
    /// <summary>Glare below this absolute radiance is not drawn (the window edge).</summary>
    public const float WindowThreshold = 2.0e-4f;
    public const float WindowMaxDegrees = 20.0f;
    private const string EffectScript = "res://scripts/render/RingSunGlareComposite.gd";
    private const double StormHideSeconds = 0.1;

    public static RingSunGlare? Instance { get; private set; }
    /// <summary>"Sun glare" setting, 0 to 1 (persisted by AtmosphereVisualDefaults).</summary>
    public static float Strength { get; private set; } = 1.0f;
    /// <summary>"Lens flare" setting, 0 to 1 (persisted; ghosts and starburst are not drawn yet).</summary>
    public static float LensFlare { get; private set; }
    /// <summary>Debug only, never persisted and never in the menu: multiplies the displayed sun radiance in the draw (comparison stills). 1 = physical.</summary>
    public static float GlareSourceGain { get; set; } = 1.0f;
    public static void SetSettings(float glare, float lensFlare)
    {
        Strength = float.IsFinite(glare) ? Mathf.Clamp(glare, 0.0f, 1.0f) : 1.0f;
        LensFlare = float.IsFinite(lensFlare) ? Mathf.Clamp(lensFlare, 0.0f, 1.0f) : 0.0f;
    }

    /// <summary>Test and capture hooks for GDScript (static members are not reachable from it): the strength without touching the persisted lens-flare value, and the debug source gain.</summary>
    public void DebugSetStrength(float strength) => SetSettings(strength, LensFlare);
    public void DebugSetGain(float gain) => GlareSourceGain = gain;

    /// <summary>Test hook: replaces the world-derived scalars (negative = use the world). Eclipse coverage, storm hide, enclosure gate.</summary>
    public float ForcedEclipseCoverage { get; set; } = -1.0f;
    public float ForcedStormHide { get; set; } = -1.0f;
    public float ForcedEnclosureGate { get; set; } = -1.0f;
    /// <summary>Test hook: replaces the sun direction (zero = use the sky's).</summary>
    public Vector3 ForcedSunDirection { get; set; } = Vector3.Zero;
    /// <summary>Test hook: when false the effect is detached from the compositor (the "effect absent" reference).</summary>
    public bool AllowAttach { get; set; } = true;

    public CompositorEffect? Effect { get; private set; }
    public bool Active { get; private set; }
    public bool Attached { get; private set; }
    public float LastEclipseCoverage { get; private set; }
    public float LastStormHide { get; private set; } = 1.0f;
    public float LastEnclosureGate { get; private set; } = 1.0f;
    public float LastThetaMaxDegrees { get; private set; }
    public int Publishes { get; private set; }

    private Node? _daySide;
    private RingSunshineCloudBridge? _bridge;
    private RingDayNightCycle? _dayNight;
    private DirectionalLight3D? _sunLight;
    private WorldEnvironment? _environment;
    private GodotObject? _sunshine;
    private readonly float[] _params = new float[16];
    private ulong _stormStamp;
    private bool _stormKnown;
    private float _stormHide = 1.0f;
    private int _slotCountdown;
    private float _intensityCache = float.NaN;
    private float _thetaStrength = float.NaN, _thetaPeak = float.NaN, _thetaRadius = float.NaN;
    private float _thetaMax;
    private static readonly StringName PublishName = "publish_frame";
    private static readonly StringName SetSunshineName = "set_sunshine";
    private static readonly StringName SunDirectionName = "published_sun_direction";
    private static readonly StringName EnclosedWeightName = "published_enclosed_fog_weight";
    private static readonly StringName UnderwaterName = "current_underwater_factor";
    private static readonly StringName DiscRadiusParam = "sun_disk_angular_radius_degrees";
    private static readonly StringName DiscIntensityParam = "sun_disk_intensity";

    public override void _Ready() => Instance = this;

    public override void _ExitTree()
    {
        if (ReferenceEquals(Instance, this)) Instance = null;
        Detach();
        Effect = null;
    }

    /// <summary>CIE 1999 general disability glare function (per steradian), theta in degrees, <see cref="Age"/> 25, <see cref="Pigmentation"/> 0.5.</summary>
    public static float Gsf(float thetaDegrees)
    {
        double a4 = Math.Pow(Age / 70.0, 4.0);
        double t2 = (double)thetaDegrees * thetaDegrees;
        double x1 = 1.0 + t2 / (0.0046 * 0.0046), x2 = 1.0 + t2 / (0.045 * 0.045), x3 = 1.0 + t2 / (0.1 * 0.1);
        double a = (1.0 - 0.08 * a4) * (9.2e6 / Math.Pow(x1, 1.5) + 1.5e5 / Math.Pow(x2, 1.5));
        double b = (1.0 + 1.6 * a4) * (400.0 / x3 + 3e-8 * t2 + Pigmentation * (1300.0 / Math.Pow(x3, 1.5) + 0.8 / Math.Sqrt(x3)));
        return (float)(a + b + 2.5e-3 * Pigmentation);
    }

    /// <summary>Window half-angle (degrees): where strength x 1.2 x peak radiance x Omega x GSF(theta) falls below <see cref="WindowThreshold"/>, capped at <see cref="WindowMaxDegrees"/>.</summary>
    public static float ThetaMaxDegrees(float strength, float peakRadiance, float discRadiusRadians)
    {
        float omega = Mathf.Pi * discRadiusRadians * discRadiusRadians;
        float last = Mathf.RadToDeg(discRadiusRadians);
        for (float theta = last; theta <= WindowMaxDegrees; theta += 0.1f)
        {
            if (strength * 1.2f * peakRadiance * omega * Gsf(theta) >= WindowThreshold) last = theta; else break;
        }
        return Mathf.Min(last + 0.1f, WindowMaxDegrees);
    }

    public override void _Process(double delta)
    {
        Resolve();
        if (Strength <= 0.0f || !AllowAttach) { Publish(false); Detach(); return; }
        if (Effect == null)
        {
            if (RenderingServer.GetRenderingDevice() == null) return;
            Effect = GD.Load<GDScript>(EffectScript)?.New().AsGodotObject() as CompositorEffect;
            if (Effect == null) return;
        }
        if (!Attached || --_slotCountdown <= 0) { Attach(); _slotCountdown = 30; }

        Vector3 sun = ForcedSunDirection != Vector3.Zero ? ForcedSunDirection.Normalized() : ReadSunDirection();
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (sun.LengthSquared() < 0.25f || camera == null || _environment == null) { Publish(false); return; }

        float radiusDegrees = 0.265f;
        float intensity = 2.55f;
        Color sunColor = _sunLight != null ? _sunLight.LightColor : Colors.White;
        if (_daySide?.Get("ring_sky_material").AsGodotObject() is ShaderMaterial sky)
        {
            Variant radius = sky.GetShaderParameter(DiscRadiusParam);
            if (radius.VariantType == Variant.Type.Float) radiusDegrees = Mathf.Max(0.01f, radius.AsSingle());
            if (float.IsNaN(_intensityCache) && sky.Shader != null)
            {
                Variant d = RenderingServer.ShaderGetParameterDefault(sky.Shader.GetRid(), DiscIntensityParam);
                if (d.VariantType == Variant.Type.Float) _intensityCache = d.AsSingle();
            }
            if (!float.IsNaN(_intensityCache)) intensity = _intensityCache;
        }
        float radius_rad = Mathf.DegToRad(radiusDegrees);
        float peak = Mathf.Max(sunColor.R, Mathf.Max(sunColor.G, sunColor.B)) * intensity * GlareSourceGain;
        if (_thetaStrength != Strength || _thetaPeak != peak || _thetaRadius != radius_rad)
        {
            _thetaStrength = Strength; _thetaPeak = peak; _thetaRadius = radius_rad;
            _thetaMax = Mathf.DegToRad(ThetaMaxDegrees(Strength, peak, radius_rad));
        }
        LastThetaMaxDegrees = Mathf.RadToDeg(_thetaMax);

        // Window entirely off-screen (or the sun behind the camera): nothing to measure or draw.
        Vector3 view = camera.GlobalTransform.Basis.Inverse() * sun;
        Projection projection = camera.GetCameraProjection();
        bool visible = false;
        if (view.Z < -1.0e-4f)
        {
            Vector4 clip = projection * new Vector4(view.X, view.Y, view.Z, 1.0f);
            float tan = Mathf.Tan(Mathf.Min(_thetaMax, 1.5f));
            float ndcX = clip.X / clip.W, ndcY = clip.Y / clip.W;
            visible = Mathf.Abs(ndcX) < 1.0f + tan * Mathf.Abs(projection.X.X) && Mathf.Abs(ndcY) < 1.0f + tan * Mathf.Abs(projection.Y.Y);
        }
        if (!visible) { Publish(false); return; }

        float coverage = ForcedEclipseCoverage >= 0.0f ? ForcedEclipseCoverage : Mathf.Clamp(_dayNight?.CurrentFrame.RawEclipseCoverage ?? 0.0f, 0.0f, 1.0f);
        ulong now = Time.GetTicksMsec();   // wall clock: the capture harness holds Engine.TimeScale at 0
        if (ForcedStormHide >= 0.0f) _stormHide = ForcedStormHide;
        else if (!_stormKnown || (now - _stormStamp) * 0.001 >= StormHideSeconds) { _stormKnown = true; _stormStamp = now; _stormHide = _bridge != null ? Mathf.Clamp(_bridge.GetSunStormHide(sun), 0.0f, 1.0f) : 1.0f; }
        float gate = 1.0f;
        if (ForcedEnclosureGate >= 0.0f) gate = ForcedEnclosureGate;
        else if (_daySide != null)
        {
            float underwater = _daySide.Get(UnderwaterName).AsSingle();
            float enclosed = _daySide.Get(EnclosedWeightName).AsSingle();
            gate = 1.0f - Mathf.Clamp(Mathf.Max(underwater, enclosed), 0.0f, 1.0f);
        }
        LastEclipseCoverage = coverage; LastStormHide = _stormHide; LastEnclosureGate = gate;

        _params[0] = sun.X; _params[1] = sun.Y; _params[2] = sun.Z; _params[3] = radius_rad;
        _params[4] = sunColor.R * intensity; _params[5] = sunColor.G * intensity; _params[6] = sunColor.B * intensity; _params[7] = 0.0f;
        _params[8] = coverage; _params[9] = _stormHide; _params[10] = gate;
        _params[11] = _bridge != null ? Mathf.Clamp(_bridge.GetCameraDirectSunTransmittance(), 0.0f, 1.0f) : 1.0f;
        _params[12] = Strength; _params[13] = GlareSourceGain; _params[14] = _thetaMax; _params[15] = 0.0f;
        Publish(true);
    }

    private void Resolve()
    {
        _daySide ??= GetNodeOrNull(DaySidePath);
        _bridge ??= GetNodeOrNull<RingSunshineCloudBridge>(BridgePath);
        _dayNight ??= GetNodeOrNull<RingDayNightCycle>(DayNightPath);
        _sunLight ??= GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        if (_environment == null || !IsInstanceValid(_environment))
        {
            _environment = GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath);
            if (_environment == null && GetViewport() is { } viewport)
                foreach (Node candidate in viewport.FindChildren("*", "WorldEnvironment", true, false))
                    if (candidate is WorldEnvironment owner && owner.GetViewport() == viewport) { _environment = owner; break; }
        }
    }

    private Vector3 ReadSunDirection()
    {
        if (_daySide == null) return Vector3.Zero;
        Variant v = _daySide.Get(SunDirectionName);
        return v.VariantType == Variant.Type.Vector3 ? v.AsVector3() : Vector3.Zero;
    }

    private void Attach()
    {
        if (Effect == null || _environment == null || !IsInstanceValid(_environment)) return;
        RingLocalWeatherEffects.AttachLensEffect(_environment, Effect);
        Attached = true;
        GodotObject? sunshine = null;
        if (_environment.Compositor != null)
        {
            var effects = _environment.Compositor.CompositorEffects;
            using Godot.Collections.Array lifetime = (Godot.Collections.Array)effects;
            for (int i = 0; i < effects.Count; i++)
                if (((effects[i]?.GetScript().AsGodotObject() as Script)?.ResourcePath ?? "").Contains("SunshineClouds2/SunshineClouds", StringComparison.Ordinal)) { sunshine = effects[i]; break; }
        }
        if (!ReferenceEquals(sunshine, _sunshine))
        {
            _sunshine = sunshine;
            Effect.Call(SetSunshineName, sunshine == null ? default : Variant.From(sunshine));
        }
    }

    private void Detach()
    {
        if (!Attached || Effect == null) return;
        RingLocalWeatherEffects.DetachLensEffect(_environment, Effect);
        Attached = false;
        _sunshine = null;
    }

    private void Publish(bool active)
    {
        if (Effect == null) { Active = false; return; }
        if (!active && !Active) return;
        Active = active;
        Effect.Call(PublishName, _params, active);
        if (active) Publishes++;
    }
}
