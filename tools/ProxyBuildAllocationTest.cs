using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo;

/// <summary>Records real proxy-sector allocation and output hashes before buffer changes.</summary>
public partial class ProxyBuildAllocationTest : RefCounted
{
    public bool Run()
    {
        try
        {
            TerrainGenerationParameters recipe = TerrainGenerationParameters.Default();
            using var runtimeSettings = RuntimeSettings(recipe.CanonicalJson);
            using var runtime = CreateSampler(runtimeSettings);
            using var legacySettings = new VoxelTerrainSettings();
            using var legacy = CreateSampler(legacySettings, 8000);

            const int spacing = 2;
            var runtimeLand = FindCoordinate(runtime, wantWater: false, spacing);
            var runtimeOcean = FindCoordinate(runtime, wantWater: true, spacing);
            var runtimeShore = FindShoreCoordinate(runtime, spacing);
            var legacyLand = FindCoordinate(legacy, wantWater: false, spacing);
            var editColumns = new Dictionary<RingTerrainColumnCoordinate, float>
            {
                [new(runtimeLand.Around, runtimeLand.Across)] = runtime.GetPresentationSurfaceHeight(runtimeLand.Around, runtimeLand.Across) + 47.25f
            };
            var emptyRuntimeEdits = EmptyEdits(runtime);
            var editedRuntime = new RingLodSurfaceEditSnapshot(1, runtime.AroundRingVoxelLength,
                new Dictionary<RingTerrainColumnCoordinate, int>(), editColumns, true);
            var emptyLegacyEdits = EmptyEdits(legacy);

            foreach (int cells in new[] { 32, 64, 128 })
            {
                int size = cells * spacing;
                RunCase("runtime-v2-land", runtime, emptyRuntimeEdits, runtimeLand, cells, false);
                RunCase("runtime-v2-ocean", runtime, emptyRuntimeEdits, runtimeOcean, cells, false);
                RunCase("runtime-v2-shore", runtime, emptyRuntimeEdits, runtimeShore, cells, false);
                RunCase("runtime-v2-edited", runtime, editedRuntime, runtimeLand, cells, false);
                RunSectorCase("runtime-v2-seam-skirts", runtime, emptyRuntimeEdits,
                    new RingTerrainSectorCoordinate((runtime.AroundRingVoxelLength + size - 1) / size - 1,
                        runtimeLand.Across / size), cells, true);
                RunCase("legacy-land", legacy, emptyLegacyEdits, legacyLand, cells, false);
            }
            GD.Print("PROXY_BUILD_ALLOCATION_BASELINE=PASS");
            return true;
        }
        catch (Exception error)
        {
            GD.PushError("PROXY_BUILD_ALLOCATION_BASELINE=FAIL: " + error);
            return false;
        }
    }

    private static void RunCase(string label, RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits,
        CanonicalCoordinate position, int cells, bool skirts)
    {
        const int spacing = 2;
        int size = cells * spacing;
        var coordinate = new RingTerrainSectorCoordinate(position.Around / size, position.Across / size);
        RunSectorCase(label, sampler, edits, coordinate, cells, skirts);
    }

    private static void RunSectorCase(string label, RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits,
        RingTerrainSectorCoordinate coordinate, int cells, bool skirts)
    {
        const int spacing = 2;
        int size = cells * spacing;
        long canonicalAround = coordinate.AroundSector * (long)size;
        int canonicalAcross = coordinate.AcrossSector * size;
        var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero,
            CertifiedRingScale.EffectiveRadiusMeters, 0.0, 0.0, sampler.AroundRingVoxelLength,
            CertifiedRingScale.TerrainFloorMeters, CertifiedRingScale.StructuralWallTopMeters,
            new RingCanonicalPosition(canonicalAround, 0.0, canonicalAcross));

        // A fresh session is the cold-cache baseline.  A separate session's first
        // and second builds isolate the builder allocations after its sample cache warms.
        RingTerrainProxySector.AllocationFixtureSnapshot cold = RingTerrainProxySector
            .CreateAllocationFixtureSession(edits, sampler, geometry, sampler.RingAcrossWidthMeters)
            .Build(coordinate, cells, skirts);
        var warmSession = RingTerrainProxySector.CreateAllocationFixtureSession(edits, sampler, geometry, sampler.RingAcrossWidthMeters);
        _ = warmSession.Build(coordinate, cells, skirts);
        RingTerrainProxySector.AllocationFixtureSnapshot warm = warmSession.Build(coordinate, cells, skirts);
        RequireSameOutput(cold, warm, label);
        GD.Print($"PROXY_BUILD_ALLOCATION case={label} cells={cells} skirts={skirts} "
            + $"cold_alloc={cold.AllocatedBytes} warm_alloc={warm.AllocatedBytes} estimated={cold.EstimatedBytes} "
            + $"vertices={cold.VertexCount} normals={cold.NormalCount} colors={cold.ColorCount} uvs={cold.UvCount} morphs={cold.MorphCount} parent_chords={cold.ParentChordCount} "
            + $"indices={cold.IndexCount} ocean_indices={cold.OceanIndexCount} shoreline_bytes={cold.ShorelineByteCount} "
            + $"geometry_hash={cold.GeometryHash:X16} color_hash={cold.ColorHash:X16} uv_hash={cold.UvHash:X16} morph_hash={cold.MorphHash:X16} parent_chord_hash={cold.ParentChordHash:X16} "
            + $"index_hash={cold.IndexHash:X16} ocean_index_hash={cold.OceanIndexHash:X16} shoreline_hash={cold.ShorelineHash:X16}");
    }

    private static void RequireSameOutput(RingTerrainProxySector.AllocationFixtureSnapshot cold,
        RingTerrainProxySector.AllocationFixtureSnapshot warm, string label)
    {
        if ((cold with { AllocatedBytes = 0 }) != (warm with { AllocatedBytes = 0 }))
            throw new InvalidOperationException("cold/warm proxy output differs for " + label);
    }

    private static CanonicalCoordinate FindCoordinate(RingTerrainSampler sampler, bool wantWater, int spacing)
    {
        foreach (CanonicalCoordinate coordinate in CandidateCoordinates(sampler, spacing))
            if (sampler.GetTerrainSample(coordinate.Around, coordinate.Across).HasSurfaceWater == wantWater)
                return coordinate;
        throw new InvalidOperationException(wantWater ? "fixture could not locate ocean" : "fixture could not locate land");
    }

    private static CanonicalCoordinate FindShoreCoordinate(RingTerrainSampler sampler, int spacing)
    {
        foreach (CanonicalCoordinate coordinate in CandidateCoordinates(sampler, spacing))
        {
            bool centerWater = sampler.GetTerrainSample(coordinate.Around, coordinate.Across).HasSurfaceWater;
            bool aroundWater = sampler.GetTerrainSample(coordinate.Around + 128, coordinate.Across).HasSurfaceWater;
            bool acrossWater = sampler.GetTerrainSample(coordinate.Around, Math.Min(sampler.RingAcrossWidthMeters - spacing, coordinate.Across + 128)).HasSurfaceWater;
            if (centerWater != aroundWater || centerWater != acrossWater) return coordinate;
        }
        throw new InvalidOperationException("fixture could not locate shoreline");
    }

    private static IEnumerable<CanonicalCoordinate> CandidateCoordinates(RingTerrainSampler sampler, int spacing)
    {
        const int aroundSteps = 64, acrossSteps = 32;
        for (int aroundIndex = 0; aroundIndex < aroundSteps; aroundIndex++)
        for (int acrossIndex = 0; acrossIndex < acrossSteps; acrossIndex++)
        {
            // Align candidates to the largest measured sector (128 cells at 2m),
            // so every 32/64/128 fixture begins at the sampled canonical column.
            const int maximumSectorSize = 256;
            long around = (aroundIndex * sampler.AroundRingVoxelLength / aroundSteps) / maximumSectorSize * maximumSectorSize;
            int across = (acrossIndex * (sampler.RingAcrossWidthMeters - spacing) / (acrossSteps - 1)) / maximumSectorSize * maximumSectorSize;
            yield return new CanonicalCoordinate(around, across);
        }
    }

    private static RingLodSurfaceEditSnapshot EmptyEdits(RingTerrainSampler sampler) => new(0,
        sampler.AroundRingVoxelLength, new Dictionary<RingTerrainColumnCoordinate, int>(),
        new Dictionary<RingTerrainColumnCoordinate, float>(), sampler.UsesRuntimeSurfaceGeneration);

    private static VoxelTerrainSettings RuntimeSettings(string recipeJson) => new()
    {
        SurfaceGenerationRecipeJson = recipeJson,
        SurfaceGenerationContractId = RingTerrainSampler.RuntimeSurfaceGenerationContractId,
        BedrockY = -8000, SeaLevelY = 0, EnableUnified3dDensityTerrain = false,
        EnableProceduralCaves = false, EnableNaturalOverhangs = false, EnableDesignedCaveDensityComposition = false
    };

    private static RingTerrainSampler CreateSampler(VoxelTerrainSettings settings, int ceiling = 8400) => new(settings, true,
        CertifiedRingScale.AroundVoxelLength, CertifiedRingScale.EffectiveRadiusMeters,
        CertifiedRingScale.RingWidthMeters, ceiling);

    private readonly record struct CanonicalCoordinate(long Around, int Across);
}
