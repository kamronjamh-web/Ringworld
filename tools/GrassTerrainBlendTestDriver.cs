using System;
using System.Reflection;
using Godot;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tests;

public partial class GrassTerrainBlendTestDriver : RefCounted
{
    public Mesh LoadMesh() => RingGrassMeshFactory.CreateClump(2);
    public Mesh LoadIntermediateMesh() => RingGrassMeshFactory.CreateIntermediateClump(singleRibbonPerSource: true);
    public Godot.Collections.Dictionary CapturePalette()
    {
        var p = RingTerrainVisuals.CaptureTexturePalette();
        return new() { ["grass"] = p.Grass, ["desert"] = p.Desert, ["forest"] = p.Forest, ["tundra"] = p.Tundra, ["alpine"] = p.Alpine };
    }

    public bool VerifyRuntimeContract()
    {
        var layer = new RingStandingGrassLayer();
        try
        {
            using var near = new ShaderMaterial { Shader = GD.Load<Shader>("res://materials/ring_standing_grass.gdshader") };
            using var mid = new ShaderMaterial { Shader = near.Shader };
            layer.EnableGrassAppearance = true;
            var placement = layer.GrassAppearanceProfile!.CapturePlacement();
            MethodInfo configure = typeof(RingStandingGrassLayer).GetMethod("ConfigureGrassAppearance", BindingFlags.NonPublic | BindingFlags.Instance)!;
            void Apply(ShaderMaterial material) => configure.Invoke(layer, new object[] { material });
            layer.EnableDistanceLod = false;
            layer.VisibilityRadiusMeters = 100;
            Apply(near); Apply(mid);
            Require(!near.GetShaderParameter("grass_blade_shading_enabled").AsBool()
                && !mid.GetShaderParameter("grass_blade_shading_enabled").AsBool(), "blade side shading defaulted enabled");
            layer.EnableGrassBladeShading = true;
            Apply(near); Apply(mid);
            Require(near.GetShaderParameter("grass_blade_shading_enabled").AsBool()
                && mid.GetShaderParameter("grass_blade_shading_enabled").AsBool(), "blade shading toggle missed cached materials");
            layer.EnableGrassBladeShading = false;
            Apply(near); Apply(mid);
            Require(!near.GetShaderParameter("grass_blade_shading_enabled").AsBool()
                && !mid.GetShaderParameter("grass_blade_shading_enabled").AsBool(), "blade shading disable missed cached materials");
            layer.GrassTerrainBlendStartPercent = 45;
            layer.GrassTerrainBlendEndPercent = 80;
            layer.GrassTerrainLightingMatch = .4f;
            layer.VisibilityRadiusMeters = 120;
            Apply(near); Apply(mid);
            foreach (var material in new[] { near, mid })
            {
                Require(Mathf.IsEqualApprox(material.GetShaderParameter("grass_terrain_blend_start_fraction").AsSingle(), .45f), "range did not refresh both materials");
                Require(Mathf.IsEqualApprox(material.GetShaderParameter("grass_terrain_draw_distance").AsSingle(), 120f), "draw cutoff cache did not refresh");
                Require(Mathf.IsEqualApprox(material.GetShaderParameter("grass_terrain_lighting_match").AsSingle(), .4f), "lighting did not refresh");
                Require(material.GetShaderParameter("grass_terrain_average_grass").AsVector3().IsEqualApprox(RingTerrainVisuals.CaptureTexturePalette().Grass), "runtime palette differs from terrain");
            }
            Require(placement == layer.GrassAppearanceProfile.CapturePlacement(), "material controls changed placement snapshot");
            layer.GrassTerrainBlendStartPercent = 99;
            layer.GrassTerrainBlendEndPercent = 100;
            layer.VisibilityRadiusMeters = 32;
            layer.LodTransitionWidthMeters = 4;
            Apply(near);
            Require(Mathf.IsEqualApprox(near.GetShaderParameter("grass_terrain_blend_start_fraction").AsSingle(), .99f)
                && Mathf.IsEqualApprox(near.GetShaderParameter("grass_terrain_blend_end_fraction").AsSingle(), 1f), "99/100 endpoints were lost");
            layer.GrassTerrainBlendStartPercent = float.NaN;
            layer.GrassTerrainBlendEndPercent = float.NegativeInfinity;
            layer.GrassTerrainLightingMatch = float.PositiveInfinity;
            Apply(near);
            float start = near.GetShaderParameter("grass_terrain_blend_start_fraction").AsSingle();
            float end = near.GetShaderParameter("grass_terrain_blend_end_fraction").AsSingle();
            Require(float.IsFinite(start) && float.IsFinite(end) && end > start, "malformed range did not become finite and ordered");
            layer.GrassAppearanceProfile = null;
            Apply(near);
            Require(!near.GetShaderParameter("grass_appearance_enabled").AsBool(), "null appearance did not disable shader");
            return true;
        }
        finally { layer.Free(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
