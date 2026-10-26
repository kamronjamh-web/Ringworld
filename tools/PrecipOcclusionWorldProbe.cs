using System;
using Godot;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>Real-world helper of tools/precip_occlusion_world_probe.gd: compares the published occlusion map with the CPU terrain (TerrainRainExposureProvider.NaturalTop: density-refined natural surface under construction and water).</summary>
public partial class PrecipOcclusionWorldProbe : RefCounted
{
    /// <summary>Samples the texels at 1 m column centres of the map (R32F bytes, row-major). A texel counts as matching when its first hit is within 0.3 m of the CPU surface; hits above (POIs, buildings), below (water, LOD gaps) and empty texels are tallied separately.</summary>
    public Godot.Collections.Dictionary CompareOpen(Node occlusionNode, Node gridNode, Node smoothNode, byte[] mapBytes)
    {
        var occlusion = (RingPrecipitationOcclusion)occlusionNode; var grid = (VoxelChunkGridManager)gridNode; var provider = new TerrainRainExposureProvider(grid, smoothNode as RingworldTechDemo.Terrain.Editable.SmoothTerrainGrid);
        var map = new float[mapBytes.Length / 4]; Buffer.BlockCopy(mapBytes, 0, map, 0, mapBytes.Length);
        Transform3D t = occlusion.PublishedTransform;
        var sampler = grid.TerrainSampler;
        int match = 0, above = 0, empty = 0, below = 0; double worst = 0, sumAbs = 0, sumErr = 0, minErr = double.MaxValue, maxErr = double.MinValue;
        if (sampler == null) return new() { ["error"] = "no terrain sampler" };
        for (int ty = 8; ty < 504; ty++)
            for (int tx = 8; tx < 504; tx++)
            {
                float d = map[ty * 512 + tx];
                if (d <= 0) { empty++; continue; }
                float x = ((tx + 0.5f) / 512f - 0.5f) * 128f, y = (0.5f - (ty + 0.5f) / 512f) * 128f;
                Vector3 hit = t.Origin + t.Basis.X * x + t.Basis.Y * y - t.Basis.Z * d;
                var canonical = grid.WorldToCanonicalRingPosition64(hit);
                double fa = canonical.AroundMeters - Math.Floor(canonical.AroundMeters), fc = canonical.AcrossMeters - Math.Floor(canonical.AcrossMeters);
                if (Math.Abs(fa - 0.5) > 0.13 || Math.Abs(fc - 0.5) > 0.13) continue; // only texels at a column centre: the CPU surface is one value per 1 m column
                if (!provider.TryProbeColumn((long)Math.Floor(canonical.AroundMeters), (int)Math.Floor(canonical.AcrossMeters), out RainColumnProbe column)) { empty++; continue; }
                float surface = column.NaturalTop;
                double error = canonical.VerticalMeters - surface;
                sumErr += error; minErr = Math.Min(minErr, error); maxErr = Math.Max(maxErr, error);
                if (error > 0.3) { above++; continue; }
                if (error < -0.3) { below++; continue; }
                match++; worst = Math.Max(worst, Math.Abs(error)); sumAbs += Math.Abs(error);
            }
        return new() { ["within_0_3"] = match, ["hit_above_surface"] = above, ["hit_below_surface"] = below, ["empty"] = empty, ["worst"] = worst, ["mean_abs"] = match > 0 ? sumAbs / match : 0, ["error_min"] = minErr, ["error_max"] = maxErr, ["error_mean"] = sumErr / Math.Max(1, above + below + match), ["plane_vertical"] = grid.WorldToCanonicalRingPosition64(t.Origin).VerticalMeters, ["camera_vertical"] = grid.WorldToCanonicalRingPosition64(occlusion.GetViewport().GetCamera3D().GlobalPosition).VerticalMeters };
    }
}
