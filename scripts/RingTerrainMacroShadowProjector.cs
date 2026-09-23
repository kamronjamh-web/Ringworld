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
    private sealed record Receiver(WeakReference<ShaderMaterial> Material, float CoordinateScale);

    private static readonly List<Receiver> Receivers = new();
    private static Texture2D? _texture;
    private static bool _enabled;
    private static float _strength = 0.34f;
    private static float _fadeStartMeters = 3200.0f;
    private static float _fadeEndMeters = 6500.0f;

    public static int ActiveReceiverCount { get; private set; }

    public static void Register(ShaderMaterial? material, float coordinateScale = 1.0f)
    {
        if (material == null) return;
        for (int index = Receivers.Count - 1; index >= 0; index--)
        {
            if (!Receivers[index].Material.TryGetTarget(out ShaderMaterial? existing)
                || !GodotObject.IsInstanceValid(existing))
            {
                Receivers.RemoveAt(index);
                continue;
            }
            if (!ReferenceEquals(existing, material)) continue;
            Receivers[index] = new Receiver(
                Receivers[index].Material,
                Mathf.Max(0.000001f, coordinateScale));
            Apply(material, Receivers[index].CoordinateScale);
            return;
        }

        var receiver = new Receiver(
            new WeakReference<ShaderMaterial>(material),
            Mathf.Max(0.000001f, coordinateScale));
        Receivers.Add(receiver);
        Apply(material, receiver.CoordinateScale);
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
        ApplyAll();
    }

    private static void ApplyAll()
    {
        int active = 0;
        for (int index = Receivers.Count - 1; index >= 0; index--)
        {
            Receiver receiver = Receivers[index];
            if (!receiver.Material.TryGetTarget(out ShaderMaterial? material)
                || !GodotObject.IsInstanceValid(material))
            {
                Receivers.RemoveAt(index);
                continue;
            }
            Apply(material, receiver.CoordinateScale);
            active++;
        }
        ActiveReceiverCount = active;
    }

    private static void Apply(ShaderMaterial material, float coordinateScale)
    {
        material.SetShaderParameter(
            "terrain_macro_shadow_enabled",
            _enabled && _texture != null);
        if (_texture != null)
            material.SetShaderParameter("terrain_macro_shadow_texture", _texture);
        material.SetShaderParameter("terrain_macro_shadow_strength", _strength);
        material.SetShaderParameter(
            "terrain_macro_shadow_fade_start_meters",
            _fadeStartMeters * coordinateScale);
        material.SetShaderParameter(
            "terrain_macro_shadow_fade_end_meters",
            _fadeEndMeters * coordinateScale);
    }
}
