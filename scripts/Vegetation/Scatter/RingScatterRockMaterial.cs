using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Vegetation.Scatter;

/// <summary>
/// T5-7a (design B4): one ShaderMaterial per (group, LOD) on materials/ring_scatter_rock.gdshader, built from the <see cref="RockCatalog"/> (rock_group and the LOD's vertex-fetch data textures).
/// <see cref="Register"/> links them to the Terrain3D surface exactly as the grass cards are linked (surface follower with the lighting, atmosphere and biome names, plus the cloud and macro shadow projectors),
/// so rocks receive haze, shadows, cloud shadow, wetness and storm light like terrain, and a replaced alpine texture reaches them with no rock change. Main thread only.
/// </summary>
public sealed class RingScatterRockMaterial
{
    public const string ShaderPath = "res://materials/ring_scatter_rock.gdshader";
    public const int LodCount = 3;

    /// <summary>The grass cards' follower names (lighting, atmosphere, biome albedo); the two biome names the rock reads are named explicitly so they stay forwarded if the grass set changes.</summary>
    public static readonly IReadOnlySet<StringName> SurfaceFollowerNames = new HashSet<StringName>(RingGrassCardMaterialConfig.SurfaceFollowerNames)
    {
        "biome_texture_scale", "biome_alpine_albedo",
    };

    /// <summary>TEST ONLY (T5-10b harness, tools/ScatterDebugBridge.cs): the shader's rock_debug_view for every material built afterwards. 0 in the game.</summary>
    public static int DebugView;

    private readonly ShaderMaterial[,] _materials = new ShaderMaterial[3, LodCount]; // [shader rock_group, lod]
    private RingTerrain3DSurface? _surface;

    /// <summary>The shader's rock_group value: 0 boulder, 1 outcrop, 2 cobble.</summary>
    public static int ShaderGroup(RingScatterRenderGroup group) => group switch
    {
        RingScatterRenderGroup.Boulders => 0,
        RingScatterRenderGroup.Outcrops => 1,
        RingScatterRenderGroup.Cobbles => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    /// <summary>The LOD the shadow casters of a group use (design B6): boulders LOD2, outcrops LOD1. Cobbles never cast (-1).</summary>
    public static int CasterLod(RingScatterRenderGroup group) => group switch
    {
        RingScatterRenderGroup.Boulders => 2,
        RingScatterRenderGroup.Outcrops => 1,
        _ => -1,
    };

    public RingScatterRockMaterial(RockCatalog catalog)
    {
        Shader shader = GD.Load<Shader>(ShaderPath);
        Texture2D alpine = GD.Load<Texture2D>("res://materials/biomes/alpine_rock_albedo.png");
        foreach (RingScatterRenderGroup group in new[] { RingScatterRenderGroup.Boulders, RingScatterRenderGroup.Outcrops, RingScatterRenderGroup.Cobbles })
        {
            RockGroupCatalog data = catalog.Group(group);
            for (int lod = 0; lod < LodCount; lod++)
            {
                RockLodMesh mesh = data.Lods[lod];
                var material = new ShaderMaterial { Shader = shader, ResourceName = $"ScatterRock_{group}_L{lod}" };
                material.SetShaderParameter("rock_group", ShaderGroup(group));
                if (DebugView != 0) material.SetShaderParameter("rock_debug_view", DebugView);
                material.SetShaderParameter("rock_positions", mesh.Positions);
                material.SetShaderParameter("rock_normals", mesh.Normals);
                // The biome albedo is a constant of the terrain; the follower registration replays the live values later.
                material.SetShaderParameter("biome_texture_scale", .20f);
                material.SetShaderParameter("biome_alpine_albedo", alpine);
                RingCloudShadowProjector.Register(material);
                RingTerrainMacroShadowProjector.Register(material);
                _materials[ShaderGroup(group), lod] = material;
            }
        }
    }

    /// <summary>The material of one (group, LOD) batch.</summary>
    public ShaderMaterial Get(RingScatterRenderGroup group, int lod) => _materials[ShaderGroup(group), lod];

    /// <summary>Material of the group's shadow-caster batch: the caster LOD's own material. The shadow pass runs the vertex stage only (vertex fetch), so no separate variant is needed. Null for cobbles.</summary>
    public ShaderMaterial? CreateCasterMaterial(RingScatterRenderGroup group)
    {
        int lod = CasterLod(group);
        return lod < 0 ? null : Get(group, lod);
    }

    /// <summary>Links every material to the surface (null: no surface yet, the cloud and macro projector registrations stay and the call may be repeated). Idempotent for the same surface.</summary>
    public void Register(RingTerrain3DSurface? surface)
    {
        if (ReferenceEquals(surface, _surface)) return;
        Unregister();
        if (surface == null || !GodotObject.IsInstanceValid(surface)) return;
        foreach (ShaderMaterial material in _materials) surface.RegisterSurfaceFollower(material, SurfaceFollowerNames);
        _surface = surface;
    }

    public void Unregister()
    {
        if (_surface != null && GodotObject.IsInstanceValid(_surface))
            foreach (ShaderMaterial material in _materials) _surface.UnregisterSurfaceFollower(material);
        _surface = null;
    }
}
