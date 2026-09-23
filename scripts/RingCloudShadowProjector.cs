using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo;

/// <summary>
/// Distributes one bounded, camera-local cloud shadow mask to every main-world
/// terrain material. Scaled-space materials intentionally remain independent.
/// </summary>
public static class RingCloudShadowProjector
{
    private sealed class Receiver
    {
        public readonly WeakReference<ShaderMaterial> Material;
        public Snapshot Applied;
        public bool HasApplied;

        public Receiver(ShaderMaterial material) => Material = new(material);
    }

    // This is deliberately a small typed cache beside the weak receiver list,
    // rather than a parameter dictionary allocated for every receiver/update.
    // It also makes replacement GPU Texture2DRD wrappers observable by identity.
    private struct Snapshot
    {
        public bool Enabled, NearEnabled, FarEnabled, HeightProjectionEnabled;
        public Texture2D? NearTexture, FarTexture;
        public Vector3 Center, Around, Across, Up, FarCenter, FarAround, FarAcross, FarUp, SunDirection;
        public Vector2 NearOffset, FarOffset;
        public float Radius, FarRadius, RingRadius, Strength, FarStrength;
        public float NearStart, NearEnd, FarStrengthStart, FarStrengthEnd, Overlap;
    }

    private static readonly List<Receiver> Materials = new();
    private static Texture2D? _texture;
    private static Texture2D? _farTexture;
    private static ImageTexture? _clearFallbackTexture;
    private static bool _nearTextureEnabled;
    private static bool _farTextureEnabled;
    private static Vector3 _centerWorld;
    private static Vector3 _axisAround = Vector3.Forward;
    private static Vector3 _axisAcross = Vector3.Up;
    private static Vector3 _axisUp = Vector3.Left;
    private static float _radiusMeters = 50000.0f;
    private static float _strength = 0.45f;
    private static float _farStrength = 0.60f;
    private static float _nearStartMeters = 1800.0f;
    private static float _nearEndMeters = 3500.0f;
    private static float _farStrengthStartMeters = 3500.0f;
    private static float _farStrengthEndMeters = 5000.0f;
    private static float _shadowOverlapDarkening;
    private static bool _enabled;
    private static Vector3 _farCenterWorld;
    private static Vector3 _farAxisAround = Vector3.Forward;
    private static Vector3 _farAxisAcross = Vector3.Up;
    private static Vector3 _farAxisUp = Vector3.Left;
    private static Vector2 _nearUvOffset;
    private static Vector2 _farUvOffset;
    private static float _farRadiusMeters = 96000.0f;
    private static float _ringRadiusMeters;
    private static Vector3 _sunDirection;
    private static bool _heightProjectionEnabled;
    public static int ActiveReceiverCount { get; private set; }
    /// <summary>Cheap test instrumentation: projector parameter writes since reset.</summary>
    public static long ParameterWriteCount { get; private set; }
    public static bool HasExternalTextures => _texture != null || _farTexture != null;
    public static void ResetParameterWriteCount() => ParameterWriteCount = 0;

    public static void Register(ShaderMaterial? material)
    {
        if (material == null) return;
        for (int index = Materials.Count - 1; index >= 0; index--)
        {
            if (!Materials[index].Material.TryGetTarget(out ShaderMaterial? existing)
                || !GodotObject.IsInstanceValid(existing))
            {
                Materials.RemoveAt(index);
                continue;
            }
            if (ReferenceEquals(existing, material))
            {
                // Register is a public resync boundary: callers may have
                // changed a uniform directly since the prior registration.
                Materials[index].HasApplied = false;
                Apply(material, Materials[index]);
                return;
            }
        }
        Receiver receiver = new(material);
        Materials.Add(receiver);
        Apply(material, receiver);
    }

    public static void ConfigureAnalyticField(
        ShaderMaterial? material,
        double circumferenceMeters,
        float ringWidthMeters,
        int seed,
        float worldUnitsToKilometres = 0.001f,
        float fadeStartWorldUnits = 38000.0f,
        float fadeEndWorldUnits = 50000.0f,
        float analyticShadowStrength = 0.60f)
    {
        if (material == null) return;
        material.SetShaderParameter("analytic_cloud_shadow_enabled", true);
        material.SetShaderParameter(
            "analytic_cloud_shadow_fade_start_meters",
            Mathf.Max(0.0f, fadeStartWorldUnits));
        material.SetShaderParameter(
            "analytic_cloud_shadow_fade_end_meters",
            Mathf.Max(fadeStartWorldUnits + 1.0f, fadeEndWorldUnits));
        material.SetShaderParameter(
            "ring_circumference_km",
            (float)(Math.Max(1.0, circumferenceMeters) * 0.001));
        material.SetShaderParameter("ring_width_km", Mathf.Max(0.001f, ringWidthMeters * 0.001f));
        material.SetShaderParameter("cloud_seed", seed);
        material.SetShaderParameter(
            "cloud_world_units_to_km",
            Mathf.Max(0.000001f, worldUnitsToKilometres));
        material.SetShaderParameter(
            "analytic_cloud_shadow_strength",
            Mathf.Clamp(analyticShadowStrength, 0.0f, 0.8f));
    }

    public static void Update(
        Texture2D texture,
        Vector3 centerWorld,
        Vector3 axisAround,
        Vector3 axisAcross,
        float radiusMeters,
        float strength,
        float farStrength,
        float nearStartMeters,
        float nearEndMeters,
        float farStrengthStartMeters,
        float farStrengthEndMeters,
        Vector3 axisUp = default,
        float ringRadiusMeters = 0.0f)
    {
        _texture = texture;
        _nearTextureEnabled = true;
        _farTexture = null;
        _farTextureEnabled = false;
        _centerWorld = centerWorld;
        _axisAround = axisAround.Normalized();
        _axisAcross = axisAcross.Normalized();
        _axisUp = ResolveUpAxis(_axisAround, _axisAcross, axisUp);
        _radiusMeters = Mathf.Max(1.0f, radiusMeters);
        _nearUvOffset = Vector2.Zero;
        _farUvOffset = Vector2.Zero;
        _farCenterWorld = centerWorld;
        _farAxisAround = _axisAround;
        _farAxisAcross = _axisAcross;
        _farAxisUp = _axisUp;
        _ringRadiusMeters = Mathf.Max(0.0f, ringRadiusMeters);
        _sunDirection = Vector3.Zero;
        _heightProjectionEnabled = false;
        _strength = Mathf.Clamp(strength, 0.0f, 1.0f);
        _farStrength = Mathf.Clamp(farStrength, 0.0f, 1.0f);
        _nearStartMeters = Mathf.Max(0.0f, nearStartMeters);
        _nearEndMeters = Mathf.Max(_nearStartMeters + 1.0f, nearEndMeters);
        _farStrengthStartMeters = Mathf.Max(0.0f, farStrengthStartMeters);
        _farStrengthEndMeters = Mathf.Max(
            _farStrengthStartMeters + 1.0f,
            farStrengthEndMeters);
        _enabled = true;
        ApplyAll();
    }

    /// <summary>Installs the two GPU-produced occlusion cascades.  Texture values are 0=clear, 1=shadow.</summary>
    public static void UpdateCascades(
        Texture2D? nearTexture, Vector3 nearCenterWorld, Vector3 nearAround, Vector3 nearAcross, Vector3 nearUp,
        float nearRadiusMeters, Vector2 nearUvOffset,
        Texture2D? farTexture, Vector3 farCenterWorld, Vector3 farAround, Vector3 farAcross, Vector3 farUp,
        float farRadiusMeters, Vector2 farUvOffset, float ringRadiusMeters,
        Vector3 sunDirection, float strength)
    {
        _texture = nearTexture;
        _nearTextureEnabled = nearTexture != null;
        _centerWorld = nearCenterWorld;
        _axisAround = nearAround.Normalized();
        _axisAcross = nearAcross.Normalized();
        _axisUp = ResolveUpAxis(_axisAround, _axisAcross, nearUp);
        _radiusMeters = Mathf.Max(1.0f, nearRadiusMeters);
        _nearUvOffset = nearUvOffset;
        _farTexture = farTexture;
        _farTextureEnabled = farTexture != null;
        _farCenterWorld = farCenterWorld;
        _farAxisAround = farAround.Normalized();
        _farAxisAcross = farAcross.Normalized();
        _farAxisUp = ResolveUpAxis(_farAxisAround, _farAxisAcross, farUp);
        _farRadiusMeters = Mathf.Max(1.0f, farRadiusMeters);
        _farUvOffset = farUvOffset;
        _ringRadiusMeters = Mathf.Max(0.0f, ringRadiusMeters);
        _sunDirection = sunDirection.LengthSquared() > 0.000001f
            ? sunDirection.Normalized()
            : Vector3.Zero;
        _heightProjectionEnabled = _ringRadiusMeters > 1.0f
            && _sunDirection.LengthSquared() > 0.000001f;
        _strength = Mathf.Clamp(strength, 0.0f, 1.0f);
        // Both cascades represent the same optical-depth field. Their
        // different resolutions and update rates change detail, not opacity.
        _farStrength = _strength;
        _nearStartMeters = 0.0f;
        _nearEndMeters = 1.0f;
        _farStrengthStartMeters = 0.0f;
        _farStrengthEndMeters = 1.0f;
        _enabled = _nearTextureEnabled || _farTextureEnabled;
        ApplyAll();
    }

    public static void Disable()
    {
        _enabled = false;
        ApplyAll();
    }

    /// <summary>
    /// Drops references to externally owned GPU textures before their render
    /// device RIDs are recycled. Receivers keep valid bindings through a clear
    /// 1x1 fallback, even while generation is paused for profiling.
    /// </summary>
    public static void ReleaseTextures()
    {
        _texture = null;
        _farTexture = null;
        _nearTextureEnabled = false;
        _farTextureEnabled = false;
        _enabled = false;
        ApplyAll();
    }

    /// <summary>
    /// Controls how overlapping real-time, projected-cloud, and macro-terrain
    /// shadows combine. Zero uses the darkest single estimate; one compounds
    /// all estimates and intentionally produces a darker overlap.
    /// </summary>
    public static void ConfigureShadowHandoff(float overlapDarkening)
    {
        _shadowOverlapDarkening = Mathf.Clamp(overlapDarkening, 0.0f, 1.0f);
        ApplyAll();
    }

    public static void UpdateTransform(
        Vector3 centerWorld,
        Vector3 axisAround,
        Vector3 axisAcross)
    {
        _centerWorld = centerWorld;
        _axisAround = axisAround.Normalized();
        _axisAcross = axisAcross.Normalized();
        _axisUp = ResolveUpAxis(_axisAround, _axisAcross, Vector3.Zero);
        ApplyAll();
    }

    public static void UpdateCascadeTransforms(
        Vector3 nearCenterWorld, Vector3 nearAround, Vector3 nearAcross, Vector3 nearUp, Vector2 nearUvOffset,
        Vector3 farCenterWorld, Vector3 farAround, Vector3 farAcross, Vector3 farUp, Vector2 farUvOffset)
    {
        _centerWorld = nearCenterWorld; _axisAround = nearAround.Normalized(); _axisAcross = nearAcross.Normalized(); _axisUp = ResolveUpAxis(_axisAround, _axisAcross, nearUp); _nearUvOffset = nearUvOffset;
        _farCenterWorld = farCenterWorld; _farAxisAround = farAround.Normalized(); _farAxisAcross = farAcross.Normalized(); _farAxisUp = ResolveUpAxis(_farAxisAround, _farAxisAcross, farUp); _farUvOffset = farUvOffset;
        ApplyAll();
    }

    private static Vector3 ResolveUpAxis(Vector3 around, Vector3 across, Vector3 supplied)
    {
        if (supplied.LengthSquared() > 0.000001f) return supplied.Normalized();
        Vector3 derived = across.Cross(around);
        return derived.LengthSquared() > 0.000001f ? derived.Normalized() : Vector3.Left;
    }

    private static void ApplyAll()
    {
        int active = 0;
        for (int index = Materials.Count - 1; index >= 0; index--)
        {
            if (!Materials[index].Material.TryGetTarget(out ShaderMaterial? material)
                || !GodotObject.IsInstanceValid(material))
            {
                Materials.RemoveAt(index);
                continue;
            }
            Apply(material, Materials[index]);
            active++;
        }
        ActiveReceiverCount = active;
    }

    private static void Apply(ShaderMaterial material, Receiver receiver)
    {
        Texture2D fallback = GetClearFallbackTexture();
        Snapshot next = new()
        {
            Enabled = _enabled && (_nearTextureEnabled || _farTextureEnabled),
            NearEnabled = _enabled && _nearTextureEnabled, FarEnabled = _enabled && _farTextureEnabled,
            NearTexture = _texture ?? fallback, FarTexture = _farTexture ?? fallback,
            Center = _centerWorld, Around = _axisAround, Across = _axisAcross, Up = _axisUp,
            FarCenter = _farCenterWorld, FarAround = _farAxisAround, FarAcross = _farAxisAcross, FarUp = _farAxisUp,
            NearOffset = _nearUvOffset, FarOffset = _farUvOffset, Radius = _radiusMeters, FarRadius = _farRadiusMeters,
            RingRadius = _ringRadiusMeters, SunDirection = _sunDirection, HeightProjectionEnabled = _heightProjectionEnabled,
            Strength = _strength, FarStrength = _farStrength, NearStart = _nearStartMeters, NearEnd = _nearEndMeters,
            FarStrengthStart = _farStrengthStartMeters, FarStrengthEnd = _farStrengthEndMeters, Overlap = _shadowOverlapDarkening
        };
        Snapshot previous = receiver.Applied;
        bool first = !receiver.HasApplied;
        // Texture/configuration writes are only needed for a new material, a
        // lifecycle transition, or a genuinely changed static cascade setup.
        if (first || previous.Enabled != next.Enabled || previous.NearEnabled != next.NearEnabled || previous.FarEnabled != next.FarEnabled) {
            SetParameter(material, "local_cloud_shadow_enabled", next.Enabled);
            SetParameter(material, "local_cloud_shadow_near_enabled", next.NearEnabled);
            SetParameter(material, "local_cloud_shadow_far_enabled", next.FarEnabled);
        }
        if (first || !ReferenceEquals(previous.NearTexture, next.NearTexture)) SetParameter(material, "local_cloud_shadow_texture", next.NearTexture);
        if (first || !ReferenceEquals(previous.FarTexture, next.FarTexture)) SetParameter(material, "local_cloud_shadow_far_texture", next.FarTexture);
        if (first || previous.Radius != next.Radius) SetParameter(material, "local_cloud_shadow_radius_meters", next.Radius);
        if (first || previous.FarRadius != next.FarRadius) SetParameter(material, "local_cloud_shadow_far_radius_meters", next.FarRadius);
        if (first || previous.RingRadius != next.RingRadius) SetParameter(material, "local_cloud_shadow_ring_radius_meters", next.RingRadius);
        if (first || previous.SunDirection != next.SunDirection) SetParameter(material, "local_cloud_shadow_sun_direction", next.SunDirection);
        if (first || previous.HeightProjectionEnabled != next.HeightProjectionEnabled) SetParameter(material, "local_cloud_shadow_height_projection_enabled", next.HeightProjectionEnabled);
        if (first || previous.Strength != next.Strength) SetParameter(material, "local_cloud_shadow_strength", next.Strength);
        if (first || previous.FarStrength != next.FarStrength) SetParameter(material, "local_cloud_shadow_far_strength", next.FarStrength);
        if (first || previous.NearStart != next.NearStart) SetParameter(material, "local_cloud_shadow_near_start_meters", next.NearStart);
        if (first || previous.NearEnd != next.NearEnd) SetParameter(material, "local_cloud_shadow_near_end_meters", next.NearEnd);
        if (first || previous.FarStrengthStart != next.FarStrengthStart) SetParameter(material, "local_cloud_shadow_far_strength_start_meters", next.FarStrengthStart);
        if (first || previous.FarStrengthEnd != next.FarStrengthEnd) SetParameter(material, "local_cloud_shadow_far_strength_end_meters", next.FarStrengthEnd);
        if (first || previous.Overlap != next.Overlap) SetParameter(material, "shadow_overlap_darkening", next.Overlap);
        // Camera-following transforms and scroll offsets are the only values
        // normally changing per frame; each receives an independent write.
        if (first || previous.Center != next.Center) SetParameter(material, "local_cloud_shadow_center_world", next.Center);
        if (first || previous.Around != next.Around) SetParameter(material, "local_cloud_shadow_axis_around", next.Around);
        if (first || previous.Across != next.Across) SetParameter(material, "local_cloud_shadow_axis_across", next.Across);
        if (first || previous.Up != next.Up) SetParameter(material, "local_cloud_shadow_axis_up", next.Up);
        if (first || previous.NearOffset != next.NearOffset) SetParameter(material, "local_cloud_shadow_uv_offset", next.NearOffset);
        if (first || previous.FarCenter != next.FarCenter) SetParameter(material, "local_cloud_shadow_far_center_world", next.FarCenter);
        if (first || previous.FarAround != next.FarAround) SetParameter(material, "local_cloud_shadow_far_axis_around", next.FarAround);
        if (first || previous.FarAcross != next.FarAcross) SetParameter(material, "local_cloud_shadow_far_axis_across", next.FarAcross);
        if (first || previous.FarUp != next.FarUp) SetParameter(material, "local_cloud_shadow_far_axis_up", next.FarUp);
        if (first || previous.FarOffset != next.FarOffset) SetParameter(material, "local_cloud_shadow_far_uv_offset", next.FarOffset);
        receiver.Applied = next;
        receiver.HasApplied = true;
    }

    private static void SetParameter(ShaderMaterial material, StringName name, Variant value)
    {
        material.SetShaderParameter(name, value);
        ParameterWriteCount++;
    }

    private static Texture2D GetClearFallbackTexture()
    {
        if (_clearFallbackTexture != null && GodotObject.IsInstanceValid(_clearFallbackTexture))
            return _clearFallbackTexture;

        Image image = Image.CreateFromData(
            1,
            1,
            false,
            Image.Format.Rgba8,
            new byte[] { 0, 0, 0, 255 });
        _clearFallbackTexture = ImageTexture.CreateFromImage(image);
        return _clearFallbackTexture;
    }
}
