using System.Runtime.CompilerServices;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Atmosphere;

/// <summary>
/// Owner of the main-camera atmosphere ray frame as global shader parameters. The nine uniforms hold one value
/// for every main-viewport material (trees, proxy terrain and ocean, POI architecture, containment field), so they
/// are set once per frame, per field, only when they change, instead of on every material. The globals are named
/// like the uniforms (<c>ring_atmosphere_*</c>); shaders opt in with <c>#define RING_ATMOSPHERE_FRAME_GLOBAL</c>
/// before their includes. Far-viewport and plugin materials keep per-material uniforms (another coordinate scale).
/// </summary>
public static class RingAtmosphereFrameGlobals
{
    private static readonly StringName ValidName = "ring_atmosphere_ray_frame_valid";
    private static readonly StringName CameraWorldName = "ring_atmosphere_camera_world_position";
    private static readonly StringName CameraHeightName = "ring_atmosphere_camera_height_meters";
    private static readonly StringName OutwardName = "ring_atmosphere_outward_world";
    private static readonly StringName AxisName = "ring_atmosphere_axis_world";
    private static readonly StringName SidewallNegativeName = "ring_atmosphere_sidewall_negative_meters";
    private static readonly StringName SidewallPositiveName = "ring_atmosphere_sidewall_positive_meters";
    private static readonly StringName CoordinateScaleName = "ring_atmosphere_coordinate_scale";
    private static readonly StringName ToSunName = "ring_atmosphere_to_sun";

    private const string DefineToken = "RING_ATMOSPHERE_FRAME_GLOBAL";

    private static bool _initialized;
    private static bool _publishedValid;
    private static RingAtmosphereRayFrame _published = RingAtmosphereRayFrame.Invalid;
    private static long _writes;
    // Weak keys: replacement shaders must not outlive their materials.
    private static readonly ConditionalWeakTable<Shader, StrongBox<bool>> ShaderUsesGlobals = new();

    /// <summary>Total global parameter writes since start (tests and diagnostics).</summary>
    public static long Writes => _writes;

    /// <summary>The frame currently held by the globals.</summary>
    public static RingAtmosphereRayFrame Published => _published;

    /// <summary>
    /// The value last written to a frame global, by uniform name. Headless runs use a dummy renderer whose
    /// RenderingServer.GlobalShaderParameterGet returns nothing, so tests read this managed copy.
    /// </summary>
    public static Variant PublishedValue(string name) => name switch
    {
        "ring_atmosphere_ray_frame_valid" => _publishedValid,
        "ring_atmosphere_camera_world_position" => _published.CameraWorld,
        "ring_atmosphere_camera_height_meters" => _published.CameraHeightMeters,
        "ring_atmosphere_outward_world" => _published.OutwardWorld,
        "ring_atmosphere_axis_world" => _published.AxisWorld,
        "ring_atmosphere_sidewall_negative_meters" => _published.SidewallNegativeMeters,
        "ring_atmosphere_sidewall_positive_meters" => _published.SidewallPositiveMeters,
        "ring_atmosphere_coordinate_scale" => _published.CoordinateScale,
        "ring_atmosphere_to_sun" => _published.ToSun,
        _ => default,
    };

    /// <summary>Test hook: forgets what was published so the next frame writes every field.</summary>
    public static void Reset() { _initialized = false; _publishedValid = false; _published = RingAtmosphereRayFrame.Invalid; }

    /// <summary>
    /// True when the material's shader reads the frame from the globals. Decided once per shader from its source
    /// (the define sits in the top-level shader), so runtime-built shader variants classify correctly too.
    /// </summary>
    public static bool UsesGlobals(ShaderMaterial? material)
    {
        Shader? shader = material?.Shader;
        if (shader == null) return false;
        return ShaderUsesGlobals.GetValue(shader, static s => new StrongBox<bool>(s.Code.Contains(DefineToken))).Value;
    }

    /// <summary>Publishes a valid <paramref name="frame"/> (invalid frames are ignored), writing only the fields that differ. Every publisher must pass the same fresh (unthrottled) sun, so the last writer of a frame holds the same value as the others.</summary>
    public static void Publish(in RingAtmosphereRayFrame frame)
    {
        // A subsystem without its own camera or grid inputs must not invalidate the shared frame for everyone.
        if (!frame.WouldPublishValid()) return;
        Write(frame);
    }

    /// <summary>Clears the frame (shaders return identity haze). Only for a truly invalid main-camera frame; subsystems that merely lack inputs must skip publishing.</summary>
    public static void Invalidate() => Write(RingAtmosphereRayFrame.Invalid);

    private static void Write(in RingAtmosphereRayFrame frame)
    {
        if (_initialized && _published == frame) return;
        bool valid = frame.WouldPublishValid();
        bool full = !_initialized || !_publishedValid;
        RingAtmosphereRayFrame previous = _published;
        int writes = 0;
        void Set(StringName name, Variant value)
        {
            RenderingServer.GlobalShaderParameterSet(name, value);
            writes++;
        }
        if (!_initialized || _publishedValid != valid) Set(ValidName, valid);
        if (valid)
        {
            if (full || !previous.CameraWorld.Equals(frame.CameraWorld)) Set(CameraWorldName, frame.CameraWorld);
            if (full || !previous.CameraHeightMeters.Equals(frame.CameraHeightMeters)) Set(CameraHeightName, frame.CameraHeightMeters);
            if (full || !previous.OutwardWorld.Equals(frame.OutwardWorld)) Set(OutwardName, frame.OutwardWorld);
            if (full || !previous.AxisWorld.Equals(frame.AxisWorld)) Set(AxisName, frame.AxisWorld);
            if (full || !previous.SidewallNegativeMeters.Equals(frame.SidewallNegativeMeters)) Set(SidewallNegativeName, frame.SidewallNegativeMeters);
            if (full || !previous.SidewallPositiveMeters.Equals(frame.SidewallPositiveMeters)) Set(SidewallPositiveName, frame.SidewallPositiveMeters);
            if (full || !previous.CoordinateScale.Equals(frame.CoordinateScale)) Set(CoordinateScaleName, frame.CoordinateScale);
            if (full || !previous.ToSun.Equals(frame.ToSun)) Set(ToSunName, frame.ToSun);
        }
        _published = frame;
        _publishedValid = valid;
        _initialized = true;
        _writes += writes;
        if (writes > 0) RingMaterialWriteCounters.Record(RingMaterialWriteCounters.Site.AtmosphereGlobals, writes);
    }
}
