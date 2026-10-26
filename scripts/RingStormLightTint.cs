using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo;

/// <summary>
/// Storm light for materials the ring shaders do not own (docs/storm-revamp-s0-storm-light-design.md, WO-4). Ring materials read the
/// storm level per fragment (ring_storm_light.gdshaderinc); a plain StandardMaterial3D cannot, and with the Environment ambient held
/// at its clear value while the storm map is valid it would otherwise stay as bright as in clear air. The one shared path: a shaded
/// StandardMaterial3D that is visible in gameplay registers here, and <see cref="Apply"/> (driven by RingSunshineCloudBridge from the
/// storm level at the camera) scales its albedo by the same ambient multiplier A(D) the ring materials use. Never register a ring
/// ShaderMaterial (it would be dimmed twice) or an unshaded / emissive-only material.
/// Inventory of gameplay-visible StandardMaterial3D (2026-10-25): SmoothConstructionOverlay concrete and glass (construction pieces),
/// SmoothTerrainGrid and VoxelChunkNode fallback surfaces, the VoxelChunkNode fluid surface. Not registered: rain/snow particles, the
/// eclipse zenith gizmo and the sun disc (unshaded or emissive), minimap and pause-menu debug meshes (UI / debug), BiomeAuthoring and
/// Sunblocker demo scenes (tools). The player has no body mesh and there are no held items.
/// Wet world (docs/storm-revamp-s2-wet-world-design.md 2.4): the same registered materials also follow the CPU mirror of the wetness at the camera
/// (<see cref="ApplyWetness"/>, driven by RingWetWorld): albedo darkens by the soak and roughness drops with the gloss. Ring ShaderMaterials read the wet map per fragment instead.
/// </summary>
public static class RingStormLightTint
{
    private sealed record Entry(WeakReference<BaseMaterial3D> Material, Color BaseAlbedo, float BaseRoughness, float BaseSpecular);
    private static readonly List<Entry> Entries = new();
    private static float _applied = 1.0f;
    private static float _sunFade;
    private static float _wetDarken = 1.0f, _wetGloss, _wetRoughness = 0.18f;

    /// <summary>Registers the material (its current albedo becomes the clear-air albedo) and returns it unchanged.</summary>
    public static T Register<T>(T material) where T : BaseMaterial3D
    {
        lock (Entries)
        {
            Entries.Add(new Entry(new WeakReference<BaseMaterial3D>(material), material.AlbedoColor, material.Roughness, material.MetallicSpecular));
            ApplyTo(material, material.AlbedoColor, material.Roughness, material.MetallicSpecular);
        }
        return material;
    }

    /// <summary>Scales every registered albedo by factor (1 = clear air). Cheap no-op while the factor is unchanged.</summary>
    public static void Apply(float factor)
    {
        factor = Mathf.Clamp(factor, 0.0f, 1.0f);
        if (MathF.Abs(factor - _applied) < 0.004f && !(factor >= 1.0f && _applied < 1.0f)) return;
        _applied = factor;
        RefreshAll();
    }

    /// <summary>Sun fade F(D at the camera) x darkness strength (0 = clear): scales every registered material's specular so a closed storm shows no sun glint on them.</summary>
    public static void ApplySunFade(float fade)
    {
        fade = Mathf.Clamp(fade, 0.0f, 1.0f);
        if (MathF.Abs(fade - _sunFade) < 0.004f && !(fade <= 0.0f && _sunFade > 0.0f)) return;
        _sunFade = fade;
        RefreshAll();
    }

    /// <summary>
    /// Wet world CPU mirror at the camera: soak darkens (albedo x (1 - 0.55 x 0.5 x soak term)), gloss lowers the roughness to wetRoughness. soak 0 and gloss 0 restore the dry look.
    /// Cheap no-op while the values are unchanged.
    /// </summary>
    public static void ApplyWetness(float soak, float film, float strength, float wetRoughness)
    {
        strength = float.IsFinite(strength) ? Mathf.Clamp(strength, 0.0f, 1.5f) : 1.0f;
        float darken = 1.0f - 0.55f * 0.5f * Mathf.Min(1.0f, SmoothStep(0.0f, 0.5f, soak) * strength);
        float gloss = Mathf.Min(1.0f, SmoothStep(0.25f, 0.85f, film) * strength);
        wetRoughness = float.IsFinite(wetRoughness) ? Mathf.Clamp(wetRoughness, 0.05f, 0.6f) : 0.18f;
        if (MathF.Abs(darken - _wetDarken) < 0.004f && MathF.Abs(gloss - _wetGloss) < 0.01f && MathF.Abs(wetRoughness - _wetRoughness) < 0.004f) return;
        _wetDarken = darken; _wetGloss = gloss; _wetRoughness = wetRoughness;
        RefreshAll();
    }

    private static float SmoothStep(float e0, float e1, float x)
    {
        float t = Mathf.Clamp((x - e0) / (e1 - e0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    private static void RefreshAll()
    {
        lock (Entries)
        {
            for (int index = Entries.Count - 1; index >= 0; index--)
            {
                Entry entry = Entries[index];
                if (!entry.Material.TryGetTarget(out BaseMaterial3D? material) || !GodotObject.IsInstanceValid(material))
                {
                    Entries.RemoveAt(index);
                    continue;
                }
                ApplyTo(material, entry.BaseAlbedo, entry.BaseRoughness, entry.BaseSpecular);
            }
        }
    }

    private static void ApplyTo(BaseMaterial3D material, Color baseAlbedo, float baseRoughness, float baseSpecular)
    {
        float specular = baseSpecular * (1.0f - 0.9f * _sunFade); // a closed storm has no sun glint: the shared sun fade F(D) x strength takes the specular down
        if (!Mathf.IsEqualApprox(material.MetallicSpecular, specular)) material.MetallicSpecular = specular;
        float factor = _applied * _wetDarken;
        if (factor < 1.0f) material.AlbedoColor = Scale(baseAlbedo, factor);
        else if (material.AlbedoColor != baseAlbedo) material.AlbedoColor = baseAlbedo;
        float roughness = Mathf.Lerp(baseRoughness, Mathf.Min(baseRoughness, _wetRoughness), _wetGloss);
        if (!Mathf.IsEqualApprox(material.Roughness, roughness)) material.Roughness = roughness;
    }

    /// <summary>Currently applied factor (diagnostics and tests).</summary>
    public static float AppliedFactor => _applied;
    /// <summary>Currently applied wet darkening multiplier (1 = dry) and gloss (diagnostics and tests).</summary>
    public static float AppliedWetDarken => _wetDarken;
    public static float AppliedWetGloss => _wetGloss;

    private static Color Scale(Color color, float factor) => new(color.R * factor, color.G * factor, color.B * factor, color.A);
}
