using System;
using System.Threading;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T2R-9: strip graphs for tests that need a river world's graph without baking the whole ring (the store's test hook installs them, S12). The bake is the production CPU path on a strip of whole columns.
/// </summary>
internal static class RiverTestGraphs
{
    /// <summary>A strip graph of <paramref name="recipe"/> (CPU source, climate captured from <paramref name="settings"/> the way the loading stage does).</summary>
    public static RiverGraph Strip(TerrainGenerationParameters recipe, VoxelTerrainSettings settings, double startMeters, int kilometres, int workers, ulong identity)
    {
        HydrologyBakeInputs inputs = HydrologyBakeInputs.Capture(settings, recipe, workers, HydrologySourcePreference.Cpu, "");
        var source = new CpuDrainageHeightSource(recipe, inputs.Climate, workers);
        DrainageGridSpec spec = DrainageGridSpec.Strip(recipe, startMeters, kilometres);
        DrainageGrid grid = source.SampleGrid(spec, null, CancellationToken.None);
        DrainageField field = DrainageBake.Bake(grid, recipe, workers, null, CancellationToken.None);
        RiverNetwork net = RiverGraphBuilder.Build(field, grid, recipe, new RiverBuildOptions { Source = source, Workers = workers }, CancellationToken.None, out _);
        return RiverGraph.Build(net, field, grid, identity, 0);
    }

    /// <summary>Installs a strip graph as the resident graph of <paramref name="recipe"/>, so a runtime sampler of that recipe can be built; returns the identity.</summary>
    public static ulong Install(TerrainGenerationParameters recipe, VoxelTerrainSettings settings, double startMeters = 10_134_000d, int kilometres = 40, int workers = 2)
    {
        ulong identity = HydrologyBakeIdentity.Compute(recipe);
        RiverGraphStore.InstallForTest(identity, Strip(recipe, settings, startMeters, kilometres, workers, identity));
        return identity;
    }
}
