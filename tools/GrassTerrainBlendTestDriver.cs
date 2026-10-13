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
    public Mesh LoadMeadowMesh(string tier) => new RingMeadowGrassMeshLibrary().Get(0, tier, 1f);
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
            layer.EnableCoordinatedGrassHandoff = false;
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
            VerifyCoordinatedContract(layer, near, mid, Apply);
            VerifyCoordinatedPublisherLifecycle();
            return true;
        }
        finally { layer.Free(); }
    }

    private static void VerifyCoordinatedContract(RingStandingGrassLayer layer, ShaderMaterial near,
        ShaderMaterial mid, Action<ShaderMaterial> apply)
    {
        layer.EnableGrassAppearance = true;
        layer.EnableCoordinatedGrassHandoff = true;
        layer.EnableDistanceLod = true;
        layer.LodNearDistanceMeters = 16;
        layer.LodFarDistanceMeters = 500;
        layer.LodTransitionWidthMeters = 16;
        layer.SpatialTileMeters = 16;
        layer.MaximumResidentTiles = 256;
        layer.MaximumInstancesPerTile = 1000;
        layer.MaximumInstances = 256000;
        layer.GrassTerrainBlendStartPercent = 70;
        layer.GrassTerrainBlendEndPercent = 90;
        float capacityLimited = layer.EffectiveGrassDrawDistance();
        Require(capacityLimited > 16 && capacityLimited < 500, "resident tile cap did not limit handoff");
        apply(near); apply(mid);
        foreach (var material in new[] { near, mid })
        {
            Require(material.GetShaderParameter("grass_coordinated_handoff_enabled").AsBool(), "coordinated shader flag missing");
            Require(Mathf.IsEqualApprox(material.GetShaderParameter("grass_terrain_draw_distance").AsSingle(), capacityLimited), "paired materials use different residency cutoff");
        }
        Vector2 band = layer.CoordinatedGrassHandoffBand();
        Require(band.X < band.Y && band.Y <= capacityLimited - 8, "handoff did not finish before retirement");
        layer.MaximumInstances = 64000;
        Require(layer.EffectiveGrassDrawDistance() < capacityLimited, "root budget did not reduce sustainable window");
        layer.EnableCoordinatedGrassHandoff = false;
        Require(Mathf.IsEqualApprox(layer.EffectiveGrassDrawDistance(), 500), "manual mode lost configured draw radius");
        apply(near);
        Require(!near.GetShaderParameter("grass_coordinated_handoff_enabled").AsBool(), "mode toggle missed cached material");
        layer.EnableCoordinatedGrassHandoff = true;
        layer.MaximumResidentTiles = 1;
        layer.LodTransitionWidthMeters = 10000;
        layer.GrassTerrainBlendStartPercent = 99;
        layer.GrassTerrainBlendEndPercent = 100;
        band = layer.CoordinatedGrassHandoffBand();
        Require(float.IsFinite(band.X) && float.IsFinite(band.Y) && band.X >= 0 && band.Y > band.X,
            "tiny window and wide retirement produced invalid handoff");
        layer.EnableDistanceLod = false;
        layer.VisibilityRadiusMeters = 25;
        layer.EnableCoordinatedGrassHandoff = false;
        Require(Mathf.IsEqualApprox(layer.EffectiveGrassDrawDistance(), 25), "non-LOD handoff missed visibility radius");
        layer.SpatialTileMeters = float.NaN;
        layer.VisibilityRadiusMeters = float.PositiveInfinity;
        layer.EnableCoordinatedGrassHandoff = true;
        Require(float.IsFinite(layer.EffectiveGrassDrawDistance()), "invalid settings produced non-finite cutoff");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void VerifyCoordinatedPublisherLifecycle()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) throw new InvalidOperationException("publisher test requires SceneTree");
        var parent = new Node3D();
        var first = new RingStandingGrassLayer { EnableGrassAppearance = true, ProcessMode = Node.ProcessModeEnum.Disabled };
        var second = new RingStandingGrassLayer { EnableGrassAppearance = true, ProcessMode = Node.ProcessModeEnum.Disabled };
        MethodInfo refresh = typeof(RingStandingGrassLayer).GetMethod("RefreshCoordinatedGrassHandoffGlobals", BindingFlags.NonPublic | BindingFlags.Instance)!;
        void Refresh(RingStandingGrassLayer target) => refresh.Invoke(target, null);
        try
        {
            tree.Root.AddChild(parent);
            parent.AddChild(first); parent.AddChild(second);
            Refresh(first); Refresh(second);
            Require(RingStandingGrassLayer.CoordinatedGrassHandoffPublisherIdForTests == first.GetInstanceId(), "second layer stole active terrain publisher");
            second.Enabled = false; Refresh(second);
            Require(RingStandingGrassLayer.CoordinatedGrassHandoffPublisherIdForTests == first.GetInstanceId(), "disabled nonowner cleared active publisher");
            second.Enabled = true; Refresh(second);
            first.Enabled = false; Refresh(first);
            Require(RingStandingGrassLayer.CoordinatedGrassHandoffPublisherIdForTests == second.GetInstanceId(), "eligible layer did not reclaim disabled publisher");
            second.Visible = false; Refresh(second);
            Require(RingStandingGrassLayer.CoordinatedGrassHandoffPublisherIdForTests == 0, "hidden layer retained terrain publisher");
            first.Enabled = true; parent.Visible = false; Refresh(first);
            Require(RingStandingGrassLayer.CoordinatedGrassHandoffPublisherIdForTests == 0, "hidden ancestor allowed publication");
            parent.Visible = true; Refresh(first);
            second.Visible = true; Refresh(second);
            typeof(RingStandingGrassLayer).GetProperty("StreamingSuspended")!.SetValue(first, true);
            Refresh(first);
            Require(RingStandingGrassLayer.CoordinatedGrassHandoffPublisherIdForTests == second.GetInstanceId(), "suspended publisher did not release to active layer");
            parent.RemoveChild(second); second.Free();
            Require(RingStandingGrassLayer.CoordinatedGrassHandoffPublisherIdForTests == 0, "exiting layer retained terrain publisher");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(second) && !second.IsInsideTree()) second.Free();
            parent.Free();
        }
    }
}
