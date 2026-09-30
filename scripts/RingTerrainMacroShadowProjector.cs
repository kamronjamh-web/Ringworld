using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo;

/// <summary>
/// Distributes the low-resolution whole-ring terrain sun-occlusion mask to
/// regional, global, and scaled-space materials. Each receiver keeps its own
/// coordinate scale so the main/far viewport handoff uses the same real range.
/// </summary>
public static class RingTerrainMacroShadowProjector
{
    private sealed record Receiver(WeakReference<GodotObject> Material, float CoordinateScale, Basis DirectionTransform);

    private static readonly List<Receiver> Receivers = new();
    private static Texture2D? _texture;
    private static bool _enabled;
    private static float _strength = 0.34f;
    private static float _fadeStartMeters = 3200.0f;
    private static float _fadeEndMeters = 6500.0f;
    private static Texture2D? _planetTexture;
    private static bool _planetEnabled;
    private static Vector3 _planetDirection = Vector3.Right;
    private static float _planetStrength = 0.34f;
    private static float _planetFadeStartMeters = 3200.0f;
    private static float _planetFadeEndMeters = 6500.0f;

    public static int ActiveReceiverCount { get; private set; }

    public static void Register(GodotObject? material, float coordinateScale = 1.0f)
    {
        if (material == null || !GodotObject.IsInstanceValid(material)
            || (material is not ShaderMaterial && !material.HasMethod("set_shader_param"))) return;
        for (int index = Receivers.Count - 1; index >= 0; index--)
        {
            if (!Receivers[index].Material.TryGetTarget(out GodotObject? existing)
                || !GodotObject.IsInstanceValid(existing))
            {
                Receivers.RemoveAt(index);
                continue;
            }
            if (!ReferenceEquals(existing, material)) continue;
            Receivers[index] = new Receiver(
                Receivers[index].Material,
                Mathf.Max(0.000001f, coordinateScale),
                Receivers[index].DirectionTransform);
            Apply(material, Receivers[index].CoordinateScale, Receivers[index].DirectionTransform);
            return;
        }

        var receiver = new Receiver(
            new WeakReference<GodotObject>(material),
            Mathf.Max(0.000001f, coordinateScale), Basis.Identity);
        Receivers.Add(receiver);
        Apply(material, receiver.CoordinateScale, receiver.DirectionTransform);
    }

    public static void SetReceiverDirectionTransform(GodotObject? material, Basis worldToReceiver)
    {
        if (material == null || !GodotObject.IsInstanceValid(material)) return;
        for (int index = Receivers.Count - 1; index >= 0; index--)
        {
            Receiver receiver = Receivers[index];
            if (!receiver.Material.TryGetTarget(out GodotObject? existing)
                || !GodotObject.IsInstanceValid(existing))
            {
                Receivers.RemoveAt(index);
                continue;
            }
            if (!ReferenceEquals(existing, material)) continue;
            if (receiver.DirectionTransform.IsEqualApprox(worldToReceiver)) return;
            Receivers[index] = receiver with { DirectionTransform = worldToReceiver };
            SetParameter(material, "terrain_planet_shadow_direction", worldToReceiver * _planetDirection);
            return;
        }
    }

    public static void Update(
        Texture2D texture,
        float strength = 0.34f,
        float fadeStartMeters = 3200.0f,
        float fadeEndMeters = 6500.0f)
    {
        _texture = texture;
        _strength = Mathf.Clamp(strength, 0.0f, 0.8f);
        _fadeStartMeters = Mathf.Max(0.0f, fadeStartMeters);
        _fadeEndMeters = Mathf.Max(_fadeStartMeters + 1.0f, fadeEndMeters);
        _enabled = true;
        ApplyAll();
    }

    public static void Disable()
    {
        _enabled = false;
        _planetEnabled = false;
        ApplyAll();
    }

    public static void UpdatePlanet(
        Texture2D texture,
        Vector3 direction,
        float strength = 0.34f,
        float fadeStartMeters = 3200.0f,
        float fadeEndMeters = 6500.0f)
    {
        _planetTexture = texture;
        _planetDirection = direction.LengthSquared() > 0.000001f ? direction.Normalized() : Vector3.Right;
        _planetStrength = Mathf.Clamp(strength, 0.0f, 0.8f);
        _planetFadeStartMeters = Mathf.Max(0.0f, fadeStartMeters);
        _planetFadeEndMeters = Mathf.Max(_planetFadeStartMeters + 1.0f, fadeEndMeters);
        _planetEnabled = true;
        ApplyAll();
    }

    public static void DisablePlanet()
    {
        if (!_planetEnabled) return;
        _planetEnabled = false;
        ApplyAll();
    }

    public static void SetPlanetHandoff(float fadeStartMeters, float fadeEndMeters)
    {
        float start = Mathf.Max(0.0f, fadeStartMeters);
        float end = Mathf.Max(start + 1.0f, fadeEndMeters);
        if (Mathf.IsEqualApprox(_planetFadeStartMeters, start)
            && Mathf.IsEqualApprox(_planetFadeEndMeters, end)) return;
        _planetFadeStartMeters = start;
        _planetFadeEndMeters = end;
        ApplyAll();
    }

    private static void ApplyAll()
    {
        int active = 0;
        for (int index = Receivers.Count - 1; index >= 0; index--)
        {
            Receiver receiver = Receivers[index];
            if (!receiver.Material.TryGetTarget(out GodotObject? material)
                || !GodotObject.IsInstanceValid(material))
            {
                Receivers.RemoveAt(index);
                continue;
            }
            Apply(material, receiver.CoordinateScale, receiver.DirectionTransform);
            active++;
        }
        ActiveReceiverCount = active;
    }

    private static void SetParameter(GodotObject material, StringName parameter, Variant value)
    {
        if (material is ShaderMaterial shaderMaterial) shaderMaterial.SetShaderParameter(parameter, value);
        else material.Call("set_shader_param", parameter, value);
    }

    private static void Apply(GodotObject material, float coordinateScale, Basis directionTransform)
    {
        SetParameter(material,
            "terrain_macro_shadow_enabled",
            _enabled && _texture != null);
        if (_texture != null)
            SetParameter(material, "terrain_macro_shadow_texture", _texture);
        SetParameter(material, "terrain_macro_shadow_strength", _strength);
        SetParameter(material,
            "terrain_macro_shadow_fade_start_meters",
            _fadeStartMeters * coordinateScale);
        SetParameter(material,
            "terrain_macro_shadow_fade_end_meters",
            _fadeEndMeters * coordinateScale);

        SetParameter(material, "terrain_planet_shadow_enabled", _planetEnabled && _planetTexture != null);
        if (_planetTexture != null)
            SetParameter(material, "terrain_planet_shadow_texture", _planetTexture);
        SetParameter(material, "terrain_planet_shadow_direction", directionTransform * _planetDirection);
        SetParameter(material, "terrain_planet_shadow_strength", _planetStrength);
        SetParameter(material,
            "terrain_planet_shadow_fade_start_meters",
            _planetFadeStartMeters * coordinateScale);
        SetParameter(material,
            "terrain_planet_shadow_fade_end_meters",
            _planetFadeEndMeters * coordinateScale);
    }
}
