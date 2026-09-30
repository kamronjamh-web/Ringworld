using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Test-only probes for the scaled regional LOD layout.</summary>
public partial class RingScaledFarViewport
{
    internal static IReadOnlyList<FarRegionalBandSpec> GetFarRegionalBandSpecsFixture(bool intermediateTiers)
        => GetFarRegionalBandSpecs(intermediateTiers);

    internal static bool AreFarRegionalBandSpecsContiguousFixture(bool intermediateTiers)
    {
        IReadOnlyList<FarRegionalBandSpec> bands = GetFarRegionalBandSpecs(intermediateTiers);
        if (bands.Count == 0) return false;
        for (int index = 0; index < bands.Count; index++)
        {
            FarRegionalBandSpec band = bands[index];
            if (band.InnerRadius < 0.0 || band.OuterRadius <= band.InnerRadius || band.SampleSpacing <= 0.0)
                return false;
            if (index > 0)
            {
                FarRegionalBandSpec previous = bands[index - 1];
                if (previous.OuterRadius != band.InnerRadius || previous.SampleSpacing >= band.SampleSpacing)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Builds actual polar-band data at the circumference seam and a clamped
    /// across edge. Adjacent bands must share their radial boundary positions;
    /// they intentionally use the same 96 angular samples.
    /// </summary>
    public static bool RunFarRegionalTransitionContract(
        RingTerrainSampler sampler,
        RingLodSurfaceEditSnapshot edits,
        out string failure)
    {
        if (sampler == null) throw new ArgumentNullException(nameof(sampler));
        IReadOnlyList<FarRegionalBandSpec> bands = GetFarRegionalBandSpecs(true);
        if (!AreFarRegionalBandSpecsContiguousFixture(true)
            || bands.Count != 7
            || bands[0].InnerRadius != 125000.0
            || bands[^1].OuterRadius != 7960000.0)
        {
            failure = "Intermediate scaled-far band layout is not the expected contiguous 512m through 32768m chain.";
            return false;
        }

        const double circumference = 8_000_000.0;
        const int width = 256;
        const float scale = 0.001f;
        long anchorAround = (long)circumference - 64L;
        int anchorAcross = 0; // Forces the negative half of every polar ring to clamp.
        double radius = circumference / Math.Tau;
        Color low = new(0.12f, 0.19f, 0.12f);
        Color high = new(0.29f, 0.27f, 0.20f);
        var built = new List<FarRegionalBandData>(bands.Count);
        foreach (FarRegionalBandSpec band in bands)
        {
            FarRegionalBandData data = BuildFarRegionalBandData(
                band.Name, sampler, anchorAround, anchorAcross,
                band.InnerRadius, band.OuterRadius, band.SampleSpacing,
                circumference, radius, width, scale, low, high, edits);
            if (data.Vertices.Length > 257 * 97)
            {
                failure = $"{band.Name} exceeded the 256 radial-row cap.";
                return false;
            }
            built.Add(data);
        }
        WeldFarRegionalBandNormals(built);

        const int row = 97;
        for (int index = 1; index < built.Count; index++)
        {
            FarRegionalBandData previous = built[index - 1];
            FarRegionalBandData next = built[index];
            int previousLastRow = previous.Vertices.Length - row;
            for (int angular = 0; angular < row; angular++)
            {
                if (!previous.Vertices[previousLastRow + angular].IsEqualApprox(next.Vertices[angular]))
                {
                    failure = $"Shared boundary mismatch between {previous.Name} and {next.Name} at angular column {angular}.";
                    return false;
                }
                if (!previous.Normals[previousLastRow + angular].IsEqualApprox(next.Normals[angular]))
                {
                    failure = $"Shared normal mismatch between {previous.Name} and {next.Name} at angular column {angular}.";
                    return false;
                }
            }
        }
        failure = string.Empty;
        return true;
    }

    /// <summary>Scene-facing contract entry point for the headless GDScript launcher.</summary>
    public string RunFarRegionalTransitionContract()
    {
        VoxelChunkGridManager? grid = _grid ?? GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        if (grid?.TerrainSampler is not RingTerrainSampler sampler)
            return "FAIL: Scaled far viewport has no terrain sampler.";
        RingLodSurfaceEditSnapshot edits = grid.CaptureLodSurfaceEditSnapshot();
        using RingTerrainSampler workerSampler = sampler.CreateIndependentCopy();
        return RunFarRegionalTransitionContract(workerSampler, edits, out string failure)
            ? "PASS"
            : $"FAIL: {failure}";
    }

    /// <summary>Standalone variant used by the headless contract launcher.</summary>
    public string RunStandaloneFarRegionalTransitionContract()
    {
        const long circumference = 8_000_000L;
        const int width = 256;
        var settings = new VoxelTerrainSettings();
        using var sampler = new RingTerrainSampler(
            settings, periodicAroundRing: true, circumference,
            circumference / Math.Tau, width, maxBuildGlobalY: 2048);
        var edits = new RingLodSurfaceEditSnapshot(
            version: 0UL, circumference,
            new Dictionary<RingTerrainColumnCoordinate, int>());
        return RunFarRegionalTransitionContract(sampler, edits, out string failure)
            ? "PASS"
            : $"FAIL: {failure}";
    }
}
