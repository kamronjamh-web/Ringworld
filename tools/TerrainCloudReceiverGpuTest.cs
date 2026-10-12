using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>Deterministic fields for rendered tests of the actual production cloud shaders.</summary>
[GlobalClass]
public partial class TerrainCloudReceiverGpuTest : RefCounted
{
    private const float Circumference = 31415926.5f;
    public ShaderMaterial CreateMaterial(string tier)
    {
        var shader = GD.Load<Shader>($"res://materials/{tier}.gdshader")
            ?? throw new InvalidOperationException($"Missing shader {tier}");
        var material = new ShaderMaterial { Shader = shader };
        var names = new HashSet<string>();
        foreach (var entry in shader.GetShaderUniformList()) names.Add(entry.AsGodotDictionary()["name"].AsString());
        void Set(string key, Variant value) { if (names.Contains(key)) material.SetShaderParameter(key, value); }
        Set("ring_atmosphere_enabled", false);
        Set("far_space_enabled", false);
        Set("far_density_enabled", false);
        Set("local_volumetric_handoff_enabled", false);
        Set("ring_circumference_km", Circumference * .001f);
        Set("ring_width_km", 318f);
        Set("ring_center", new Vector3(-4997800, 0, 0));
        Set("camera_world_position", new Vector3(0, 0, 12000));
        Set("ring_atmosphere_camera_world_position", new Vector3(0, 0, 12000));
        Set("ring_atmosphere_coordinate_scale", 1f);
        Set("surface_up", Vector3.Up);
        Set("sun_direction", tier == "ring_cloud_layer" ? Vector3.Left : Vector3.Up);
        Set("sun_direction_canonical", Vector3.Up);
        Set("terrain_cloud_receiver_height_meters", 4000f);
        Set("terrain_cloud_world_to_ring", Basis.Identity);
        Set("terrain_cloud_ring_geometry", new Vector3(5000000, 0, -159000));
        Set("canonical_origin_km", tier == "ring_local_volumetric_cloud"
            ? Variant.From(new Vector3(0, 4, 159)) : Variant.From(new Vector2(0, 159)));
        Set("camera_canonical_km", new Vector3(0, 4, 171));
        Set("volume_center_canonical_km", new Vector3(0, 4, 159));
        Set("volume_size_meters", new Vector3(8000, 8000, 8000));
        Set("cloud_base_fraction", 0f); Set("cloud_top_fraction", 1f);
        Set("cloud_base_meters", 0f); Set("cloud_top_meters", 8000f);
        Set("viewer_height_meters", 12000f);
        Set("patch_size_km", new Vector2(8, 8));
        Set("patch_radius_meters", 4000f); Set("patch_across_meters", 8000f);
        Set("handoff_start_meters", 0f); Set("handoff_end_meters", 1f);
        Set("handoff_in_start_meters", 0f);
        if (tier == "ring_mesh_cloud" || tier == "ring_cloud_transmittance_mask" || tier == "ring_local_volumetric_cloud")
        { Set("handoff_start_meters", 90000f); Set("handoff_end_meters", 100000f); }
        Set("handoff_start_fraction", 0f); Set("handoff_end_fraction", .05f);
        Set("wind_km_per_second", Vector2.Zero);
        Set("weather_time_seconds", 0f);
        Set("coverage", tier == "ring_cloud_transmittance_mask" || tier == "ring_local_volumetric_cloud" ? 1f : 0f);
        Set("density_scale", .02f); Set("extinction_per_kilometer", .5f);
        Set("opacity", .8f); Set("use_vertex_cloud_data", true);
        var weather = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
        weather.Fill(new Color(.95f, 0, .5f, .5f));
        Set("weather_atlas", ImageTexture.CreateFromImage(weather)); Set("weather_atlas_enabled", true);
        SetState(material, "clear");
        return material;
    }

    public void SetState(ShaderMaterial material, string state)
    {
        // Two altitude slices and one-texel padding exactly match the production atlas layout.
        var image = Image.CreateEmpty(6, 12, false, Image.Format.Rgf);
        image.Fill(state == "blocked" || state == "disabled" || state == "stale" || state == "out_band"
            ? new Color(0, 1, 0, 1) : state == "unknown" ? new Color(1, 0, 0, 1) : new Color(1, 1, 0, 1));
        var texture = ImageTexture.CreateFromImage(image);
        material.SetShaderParameter("terrain_cloud_sun_global_texture", texture);
        if (state == "regional_blocked")
        {
            var blocked = Image.CreateEmpty(6, 12, false, Image.Format.Rgf);
            blocked.Fill(new Color(0, 1, 0, 1));
            material.SetShaderParameter("terrain_cloud_sun_regional_texture", ImageTexture.CreateFromImage(blocked));
        }
        else material.SetShaderParameter("terrain_cloud_sun_regional_texture", texture);
        material.SetShaderParameter("terrain_cloud_sun_profile", new Godot.Collections.Array<Vector4> {
            new(Circumference, 318000, state == "out_band" ? 80000 : 0, state == "out_band" ? 100000 : 20000),
            new(4, 4, 2, state == "disabled" ? 0 : 1), new(4, 4, 32000, 0),
            new(159000, state == "stale" ? 0 : 1, state == "stale" ? 0 : 1, 1), new(5000000, 0, 0, 0) });
    }

    public Godot.Collections.Dictionary Compare(Image before, Image after)
    {
        if (before.GetSize() != after.GetSize()) throw new InvalidOperationException("Capture size changed");
        double sum = 0, alpha = 0, brightnessBefore = 0, brightnessAfter = 0;
        float max = 0, maxAlpha = 0; int count = 0;
        // Exclude silhouette edges: camera jitter or breathing cannot masquerade as a solar color change.
        for (int y = before.GetHeight()/5; y < before.GetHeight()*4/5; y++)
        for (int x = before.GetWidth()/5; x < before.GetWidth()*4/5; x++)
        {
            Color a = before.GetPixel(x,y).SrgbToLinear(), b = after.GetPixel(x,y).SrgbToLinear();
            if (!float.IsFinite(a.R+a.G+a.B+a.A+b.R+b.G+b.B+b.A))
                throw new InvalidOperationException("Non-finite rendered pixel");
            float delta = (MathF.Abs(a.R-b.R)+MathF.Abs(a.G-b.G)+MathF.Abs(a.B-b.B))/3;
            float da = MathF.Abs(a.A-b.A);
            sum += delta; alpha += da; max = Math.Max(max, delta); maxAlpha = Math.Max(maxAlpha, da);
            brightnessBefore += (a.R+a.G+a.B)/3; brightnessAfter += (b.R+b.G+b.B)/3; count++;
        }
        return new() { ["mean"] = sum/count, ["max"] = max, ["alpha_mean"] = alpha/count,
            ["alpha_max"] = maxAlpha, ["before"] = brightnessBefore/count, ["after"] = brightnessAfter/count };
    }
}
