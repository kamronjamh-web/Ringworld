using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>Bounded concrete terrain and material queries for water render fixtures.</summary>
public partial class WaterRenderingProbe : RefCounted
{
    public Godot.Collections.Dictionary FindOcean(Node game)
    {
        var result = new Godot.Collections.Dictionary { ["valid"] = false };
        VoxelChunkGridManager? grid = game.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager");
        RingTerrainSampler? sampler = grid?.TerrainSampler;
        if (grid is null || sampler is null) return result;

        const int aroundSamples = 12;
        const int acrossSamples = 8;
        double circumference = grid.GetCanonicalRingCircumferenceMeters64();
        int width = Mathf.RoundToInt(grid.RingAcrossWidthMeters);
        TerrainSample best = default;
        long bestAround = 0;
        int bestAcross = 0;
        bool found = false;
        for (int z = 1; z <= acrossSamples; z++)
        for (int x = 0; x < aroundSamples; x++)
        {
            long around = (long)((x + .5d) * circumference / aroundSamples);
            int across = Mathf.RoundToInt(z * width / (float)(acrossSamples + 1));
            TerrainSample sample = sampler.GetTerrainSample(around, across);
            if (sample.WaterKind != SurfaceWaterKind.Ocean || sample.WaterDepth <= 0) continue;
            if (!found || sample.WaterDepth > best.WaterDepth)
            {
                best = sample;
                bestAround = around;
                bestAcross = across;
                found = true;
            }
        }
        if (!found) return result;
        result["valid"] = true;
        result["around"] = bestAround;
        result["across"] = bestAcross;
        result["surface"] = best.WaterSurfaceHeight;
        result["depth"] = best.WaterDepth;
        result["water_kind"] = (int)best.WaterKind;
        return result;
    }

    public Godot.Collections.Dictionary GetFftBindingStatus(Node game)
    {
        var result = new Godot.Collections.Dictionary
        {
            ["controller_ready"] = false,
            ["controller_enabled"] = false,
            ["active_cascades"] = 0,
            ["bound_materials"] = 0,
            ["geometry_meshes"] = 0,
            ["covered_coarse_meshes"] = 0,
            ["water_ownership_meshes"] = 0,
            ["surface_query_ready"] = false,
        };
        RingWaterFftController? controller = game.GetNodeOrNull<RingWaterFftController>("WaterFftController");
        if (controller is null) return result;
        result["controller_enabled"] = controller.Enabled;
        result["controller_ready"] = controller.Ready && controller.NormalFoamTexture is not null && controller.DisplacementTexture is not null;
        result["active_cascades"] = controller.ActiveCascadeCount;
        RingVoxelExplorerController? explorer = game.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        if (explorer is not null)
            result["surface_query_ready"] = controller.TryGetOceanSurfaceOffset(explorer.CanonicalPosition, out _);
        result["geometry_distance"] = controller.EffectiveGeometryDistanceMeters;
        foreach (Node node in game.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (node is not MeshInstance3D mesh || mesh.Mesh is null) continue;
            for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                if (mesh.GetActiveMaterial(surface) is not ShaderMaterial material
                    || material.Shader?.ResourcePath != "res://materials/ring_ocean_proxy.gdshader") continue;
                if (mesh.IsVisibleInTree()
                    && material.GetShaderParameter("ocean_replacement_coverage_enabled").AsBool()
                    && mesh.GetInstanceShaderParameter("ocean_replacement_coverage_rect").AsVector4().Z > 0)
                    result["water_ownership_meshes"] = result["water_ownership_meshes"].AsInt32() + 1;
                if (material.GetShaderParameter("fft_ready").AsBool()
                    && material.GetShaderParameter("fft_normal_foam").VariantType != Variant.Type.Nil
                    && material.GetShaderParameter("fft_displacement").VariantType != Variant.Type.Nil)
                {
                    result["bound_materials"] = result["bound_materials"].AsInt32() + 1;
                    if (mesh.IsVisibleInTree())
                    {
                        if (material.GetShaderParameter("fft_geometry_spacing_meters").AsSingle() > .001f)
                            result["geometry_meshes"] = result["geometry_meshes"].AsInt32() + 1;
                        else if (material.GetShaderParameter("fft_ocean_coverage_enabled").AsBool()
                            && material.GetShaderParameter("fft_ocean_coverage_atlas").AsGodotObject() is Texture2D coverage
                            && coverage.GetWidth() == 512 && coverage.GetHeight() == 512)
                            result["covered_coarse_meshes"] = result["covered_coarse_meshes"].AsInt32() + 1;
                    }
                }
            }
        }
        return result;
    }

    /// <summary>Finds a bounded real ocean-to-dry transition, then bisects to
    /// the wet side so a fixture can stand a few metres offshore.</summary>
    public Godot.Collections.Dictionary FindOceanShore(Node game)
    {
        var result = new Godot.Collections.Dictionary { ["valid"] = false };
        VoxelChunkGridManager? grid = game.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager");
        RingTerrainSampler? sampler = grid?.TerrainSampler;
        if (grid is null || sampler is null) return result;
        double circumference = grid.GetCanonicalRingCircumferenceMeters64();
        int width = Mathf.RoundToInt(grid.RingAcrossWidthMeters);
        const int aroundSamples = 16, acrossSamples = 96;
        for (int aroundIndex = 0; aroundIndex < aroundSamples; aroundIndex++)
        {
            long around = (long)((aroundIndex + .5d) * circumference / aroundSamples);
            int previousAcross = 0;
            bool previousWet = IsOceanWet(sampler.GetTerrainSample(around, previousAcross));
            for (int acrossIndex = 1; acrossIndex <= acrossSamples; acrossIndex++)
            {
                int across = Mathf.RoundToInt(acrossIndex * width / (float)acrossSamples);
                bool wet = IsOceanWet(sampler.GetTerrainSample(around, across));
                if (wet == previousWet) { previousAcross = across; continue; }
                int dry = wet ? previousAcross : across;
                int water = wet ? across : previousAcross;
                for (int iteration = 0; iteration < 14 && Math.Abs(water - dry) > 1; iteration++)
                {
                    int midpoint = (water + dry) / 2;
                    if (IsOceanWet(sampler.GetTerrainSample(around, midpoint))) water = midpoint;
                    else dry = midpoint;
                }
                TerrainSample waterSample = sampler.GetTerrainSample(around, water);
                if (!IsOceanWet(waterSample)) continue;
                result["valid"] = true;
                result["around"] = around;
                result["offshore_across"] = water;
                result["dry_across"] = dry;
                result["transition_across"] = (water + dry) * .5f;
                result["surface"] = waterSample.WaterSurfaceHeight;
                result["depth"] = waterSample.WaterDepth;
                result["water_kind"] = (int)waterSample.WaterKind;
                return result;
            }
        }
        return result;
    }

    public Godot.Collections.Dictionary GetFftShorelineStatus(Node game, double around, int across)
    {
        var result = new Godot.Collections.Dictionary
        {
            ["field_valid"] = RingTerrainProxySector.SharedFftShorelineFieldValid,
            ["field_revision"] = (long)RingTerrainProxySector.SharedFftShorelineFieldRevision,
            ["sample_valid"] = false,
        };
        var position = new RingCanonicalPosition(around, 0.0, across);
        if (!RingTerrainProxySector.TrySampleFftShorelineField(position, out RingTerrainProxySector.FftShorelineSample sample)) return result;
        result["sample_valid"] = sample.Valid;
        result["signed_depth"] = sample.SignedDepthMeters;
        result["shore_distance"] = sample.ShoreDistanceMeters;
        return result;
    }

    public bool PlaceShoreCamera(Node game, Godot.Collections.Dictionary shore, float offshoreMeters = 8.0f)
    {
        if (!shore.TryGetValue("valid", out Variant valid) || !valid.AsBool()) return false;
        RingVoxelExplorerController? explorer = game.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        if (explorer is null) return false;
        int waterAcross = shore["offshore_across"].AsInt32();
        int dryAcross = shore["dry_across"].AsInt32();
        int direction = Math.Sign(waterAcross - dryAcross);
        int offshoreAcross = waterAcross + direction * Mathf.RoundToInt(Mathf.Clamp(offshoreMeters, 1.0f, 8.0f));
        explorer.SetBenchmarkCanonicalPosition(new RingCanonicalPosition(
            shore["around"].AsDouble(), shore["surface"].AsDouble() + 5.0, offshoreAcross), 0.0f, true);
        return explorer.ResetCanonicalOrientationForCapture();
    }

    private static bool IsOceanWet(in TerrainSample sample) =>
        sample.WaterKind == SurfaceWaterKind.Ocean && sample.WaterDepth > 0;

    public Godot.Collections.Dictionary PlaceCamera(Node game, Godot.Collections.Dictionary ocean)
    {
        var result = new Godot.Collections.Dictionary { ["valid"] = false };
        if (!ocean.TryGetValue("valid", out Variant valid) || !valid.AsBool()
            || !ocean.TryGetValue("water_kind", out Variant kind)
            || kind.AsInt32() != (int)SurfaceWaterKind.Ocean) return result;
        RingVoxelExplorerController? explorer = game.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        if (explorer is null) return result;
        double around = ocean["around"].AsDouble();
        double across = ocean["across"].AsDouble();
        double surface = ocean["surface"].AsDouble();
        double requestedY = surface + 6.0;
        explorer.SetBenchmarkCanonicalPosition(new RingCanonicalPosition(around, requestedY, across), 0.0f, true);
        if (!explorer.ResetCanonicalOrientationForCapture()) return result;
        explorer.SetPresentationView(25.0f, -28.0f);
        result["valid"] = true;
        result["requested_y"] = requestedY;
        result["actual_y"] = explorer.CanonicalPosition.VerticalMeters;
        result["surface"] = surface;
        result["water_kind"] = kind;
        return result;
    }

    /// <summary>Moves the deterministic smoke observer vertically without changing its canonical ocean sample.</summary>
    public Godot.Collections.Dictionary PlaceAerialCamera(Node game, Godot.Collections.Dictionary ocean, double heightMeters)
    {
        var result = new Godot.Collections.Dictionary { ["valid"] = false };
        if (!ocean.TryGetValue("valid", out Variant valid) || !valid.AsBool()
            || !ocean.TryGetValue("water_kind", out Variant kind)
            || kind.AsInt32() != (int)SurfaceWaterKind.Ocean
            || !double.IsFinite(heightMeters) || heightMeters < 10d || heightMeters > 10000d) return result;
        RingVoxelExplorerController? explorer = game.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        if (explorer is null) return result;
        double surface = ocean["surface"].AsDouble();
        explorer.SetBenchmarkCanonicalPosition(new RingCanonicalPosition(
            ocean["around"].AsDouble(), surface + heightMeters, ocean["across"].AsDouble()), 0.0f, true);
        if (!explorer.ResetCanonicalOrientationForCapture()) return result;
        // A fixed oblique look keeps the horizon and a large water footprint in
        // frame; input and physics are disabled by the caller after placement.
        explorer.SetPresentationView(25.0f, -48.0f);
        result["valid"] = true;
        result["requested_y"] = surface + heightMeters;
        result["actual_y"] = explorer.CanonicalPosition.VerticalMeters;
        result["surface"] = surface;
        result["height"] = heightMeters;
        return result;
    }
}
