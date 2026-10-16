using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Single C# source of every uniform value of the grass-card and hero-blade materials (grass-cards plan step S4). The card and
/// hero shaders share the colour/form uniform names (ring_grass_card_color.gdshaderinc), so feeding both from one config keeps
/// blades and cards coloured identically. Defaults are variant D.
/// </summary>
public sealed class RingGrassCardMaterialConfig
{
    public const string CardShaderPath = "res://materials/ring_grass_cards.gdshader";
    public const string HeroShaderPath = "res://materials/ring_grass_hero.gdshader";
    /// <summary>Instance uniforms declared by the card and hero shaders (Godot allows 16 per shader).</summary>
    public const int InstanceUniformCount = 9;

    // ---- Curve (mirrors RingGrassCardCurveParameters) ----
    internal RingGrassCardCurveParameters Curve { get; set; } = RingGrassCardCurveParameters.Default;
    public float MinimumGrowIn { get; set; } = RingGrassCardCurve.MinimumGrowIn;
    // ---- Cards ----
    public float AlphaCutoff { get; set; } = .5f;
    public float SingleCardStartMeters { get; set; } = 90f;
    public float SingleCardEndMeters { get; set; } = 160f;
    public float SingleCardWidthGain { get; set; } = .6f;
    public float EdgeOnStart { get; set; } = .93f;
    public float Tilt { get; set; } = .08f;
    public float ConvergeStartPercent { get; set; } = 60f;
    public float ConvergeEndPercent { get; set; } = 90f;
    public float RootDark { get; set; } = .78f;
    public float TipGain { get; set; } = 1.25f;
    public Vector3 TipShift { get; set; } = new(1.06f, 1f, .82f);
    public float ColorVariation { get; set; } = .07f;
    public float VertexLodBias { get; set; } = 3f;
    public float FragmentColorMeters { get; set; } = 70f;
    public float FragmentLodBias { get; set; } = -.5f;
    // ---- Form (cards + hero) ----
    public float NormalOut { get; set; } = .45f;
    public float NormalFace { get; set; } = .25f;
    public float Backlight { get; set; } = .28f;
    public float NormalTipStart { get; set; } = .6f;
    // ---- Colour (cards + hero, identical names) ----
    public float GreenPull { get; set; } = .15f;
    public Vector3 GrassHue { get; set; } = new(.62f, 1f, .36f);
    public float RootAo { get; set; } = .5f;
    public float TipLight { get; set; } = .16f;
    public Vector3 TipTint { get; set; } = new(1.04f, 1f, .74f);
    public float BladeValueJitter { get; set; } = .22f;
    public float BladeHueJitter { get; set; } = .10f;
    public float DryFraction { get; set; } = .07f;
    public Vector3 DryTone { get; set; } = new(.70f, .58f, .24f);
    public float TuftValueJitter { get; set; } = .14f;
    // ---- Hero ----
    public float HeroStartMeters { get; set; } = 4f;
    public float HeroEndMeters { get; set; } = 6f;
    public float HeroDitherMeters { get; set; } = 1.4f;
    public float HeroMinWidth { get; set; } = .45f;

    /// <summary>Terrain-surface shader parameters the grass materials mirror (feed to RingTerrain3DSurface.RegisterSurfaceFollower).
    /// Lighting, atmosphere (incl. the camera ray frame), anchors/frame values and the biome albedo samplers. Macro/cloud shadow
    /// textures come from their projectors' Register calls instead.</summary>
    public static readonly IReadOnlySet<StringName> SurfaceFollowerNames = new HashSet<StringName>
    {
        "sun_direction", "day_start_dot", "day_full_dot", "night_tint", "night_direct_light_factor", "day_direct_light_factor", "shadow_floor",
        "ring_viewer_world_position", "ring_width_meters", "ring_radius_meters",
        "ring_atmosphere_camera_world", "ring_atmosphere_center_world", "ring_atmosphere_enabled", "ring_atmosphere_half_width_meters",
        "ring_atmosphere_ray_frame_valid", "ring_atmosphere_camera_world_position", "ring_atmosphere_camera_height_meters",
        "ring_atmosphere_outward_world", "ring_atmosphere_axis_world", "ring_atmosphere_sidewall_negative_meters",
        "ring_atmosphere_sidewall_positive_meters", "ring_atmosphere_coordinate_scale", "ring_atmosphere_to_sun",
        "atmosphere_floor_depth_meters", "atmosphere_boundary_height_meters", "atmosphere_upper_height_meters",
        "atmosphere_boundary_extinction_meters", "atmosphere_upper_extinction_meters", "atmosphere_boundary_max_opacity",
        "atmosphere_upper_max_opacity", "atmosphere_total_max_opacity", "atmosphere_clear_distance_meters", "atmosphere_full_distance_meters",
        "atmosphere_high_altitude_clear_distance_meters", "atmosphere_high_altitude_full_distance_meters",
        "atmosphere_distance_altitude_start_meters", "atmosphere_distance_altitude_end_meters", "atmosphere_distance_haze_max_opacity",
        "atmosphere_night_density_factor", "atmosphere_scattering_energy", "atmosphere_aerosol", "atmosphere_forward_scattering",
        "atmosphere_sun_warmth", "atmosphere_day_color", "atmosphere_night_color",
        "biome_texture_scale", "biome_grass_albedo", "biome_dirt_albedo", "biome_desert_albedo", "biome_forest_albedo",
        "biome_tundra_albedo", "biome_alpine_albedo",
    };

    public ShaderMaterial CreateCardMaterial(Texture2D atlas)
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>(CardShaderPath) };
        ApplyCard(material, atlas);
        return material;
    }

    public ShaderMaterial CreateHeroMaterial()
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>(HeroShaderPath) };
        ApplyHero(material);
        return material;
    }

    public void ApplyCard(ShaderMaterial material, Texture2D? atlas = null)
    {
        if (atlas != null) material.SetShaderParameter("grass_card_atlas", atlas);
        ApplyCurve(material);
        material.SetShaderParameter("grass_card_alpha_cutoff", AlphaCutoff);
        material.SetShaderParameter("grass_card_single_start", SingleCardStartMeters);
        material.SetShaderParameter("grass_card_single_end", SingleCardEndMeters);
        material.SetShaderParameter("grass_card_single_width_gain", SingleCardWidthGain);
        material.SetShaderParameter("grass_card_edge_on_start", EdgeOnStart);
        material.SetShaderParameter("grass_card_tilt", Tilt);
        material.SetShaderParameter("grass_card_converge_start_percent", ConvergeStartPercent);
        material.SetShaderParameter("grass_card_converge_end_percent", ConvergeEndPercent);
        material.SetShaderParameter("grass_card_root_dark", RootDark);
        material.SetShaderParameter("grass_card_tip_gain", TipGain);
        material.SetShaderParameter("grass_card_tip_shift", TipShift);
        material.SetShaderParameter("grass_card_color_variation", ColorVariation);
        material.SetShaderParameter("grass_card_vertex_lod_bias", VertexLodBias);
        material.SetShaderParameter("grass_card_fragment_color_meters", FragmentColorMeters);
        ApplyShared(material);
    }

    public void ApplyHero(ShaderMaterial material)
    {
        material.SetShaderParameter("grass_hero_start", HeroStartMeters);
        material.SetShaderParameter("grass_hero_end", HeroEndMeters);
        material.SetShaderParameter("grass_hero_dither", HeroDitherMeters);
        material.SetShaderParameter("grass_hero_min_width", HeroMinWidth);
        ApplyCurve(material);
        ApplyShared(material);
    }

    private void ApplyCurve(ShaderMaterial material)
    {
        RingGrassCardCurveParameters c = Curve;
        material.SetShaderParameter("grass_card_reach", c.ReachMeters);
        material.SetShaderParameter("grass_card_near_density", c.NearDensity);
        material.SetShaderParameter("grass_card_base_density", c.BaseDensity);
        material.SetShaderParameter("grass_card_floor_density", c.FloorDensity);
        material.SetShaderParameter("grass_card_falloff_exponent", c.FalloffExponent);
        material.SetShaderParameter("grass_card_falloff_start", c.FalloffStartMeters);
        material.SetShaderParameter("grass_card_near_boost", new Vector2(c.NearBoostStartMeters, c.NearBoostEndMeters));
        material.SetShaderParameter("grass_card_comp_max", c.CompensationMax);
        material.SetShaderParameter("grass_card_near_width", c.NearWidthScale);
        material.SetShaderParameter("grass_card_fade_band_percent", c.FadeBandPercent);
        material.SetShaderParameter("grass_card_low_layer", new Vector2(c.LowLayerFullMeters, c.LowLayerEndMeters));
        material.SetShaderParameter("grass_card_hero_thin", new Vector3(c.HeroThinMinimum, c.HeroThinStartMeters, c.HeroThinEndMeters));
        material.SetShaderParameter("grass_card_min_grow_in", MinimumGrowIn);
    }

    private void ApplyShared(ShaderMaterial material)
    {
        material.SetShaderParameter("grass_card_normal_out", NormalOut);
        material.SetShaderParameter("grass_card_normal_face", NormalFace);
        material.SetShaderParameter("grass_card_backlight", Backlight);
        material.SetShaderParameter("grass_card_normal_tip_start", NormalTipStart);
        material.SetShaderParameter("grass_card_frag_lod_bias", FragmentLodBias);
        material.SetShaderParameter("grass_card_green_pull", GreenPull);
        material.SetShaderParameter("grass_card_hue", GrassHue);
        material.SetShaderParameter("grass_card_root_ao", RootAo);
        material.SetShaderParameter("grass_card_tip_light", TipLight);
        material.SetShaderParameter("grass_card_tip_tint", TipTint);
        material.SetShaderParameter("grass_card_blade_value_jitter", BladeValueJitter);
        material.SetShaderParameter("grass_card_blade_hue_jitter", BladeHueJitter);
        material.SetShaderParameter("grass_card_dry_fraction", DryFraction);
        material.SetShaderParameter("grass_card_dry_tone", DryTone);
        material.SetShaderParameter("grass_card_tuft_value_jitter", TuftValueJitter);
    }
}
