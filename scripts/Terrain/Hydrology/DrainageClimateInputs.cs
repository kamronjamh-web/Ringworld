using System;
using RingworldTechDemo.Terrain.Generation.Gpu;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>Per-worker moisture evaluator for the CPU source. Not shared between threads.</summary>
public interface IDrainageMoistureReader : IDisposable
{
    float Moisture(long around, int across);
}

/// <summary>
/// What moisture needs: the GPU climate settings (the shader reads them from the recipe buffer) and a factory for a CPU evaluator per worker.
/// Tools and tests build it from a sampler with <see cref="FromSampler"/>; T2R-9 builds it for the game without one.
/// </summary>
public sealed class DrainageClimateInputs
{
    public ProxyGpuClimateSettings Gpu { get; }
    private readonly Func<IDrainageMoistureReader> _createReader;

    public DrainageClimateInputs(ProxyGpuClimateSettings gpu, Func<IDrainageMoistureReader> createReader)
    {
        Gpu = gpu;
        _createReader = createReader;
    }

    public IDrainageMoistureReader CreateReader() => _createReader();

    /// <summary>Reads moisture through independent copies of <paramref name="root"/>, which must outlive every reader and stay a runtime-surface sampler.</summary>
    public static DrainageClimateInputs FromSampler(RingTerrainSampler root)
    {
        ProxyGpuClimateSettings gpu = root.CreateGpuClimateSettings()
            ?? throw new InvalidOperationException("The drainage climate inputs need a runtime-surface sampler.");
        return new DrainageClimateInputs(gpu, () => new SamplerReader(root.CreateIndependentCopy()));
    }

    private sealed class SamplerReader : IDrainageMoistureReader
    {
        private readonly RingTerrainSampler _sampler;
        public SamplerReader(RingTerrainSampler sampler) => _sampler = sampler;
        public float Moisture(long around, int across) => _sampler.GetTerrainSample(around, across).Moisture;
        public void Dispose() => _sampler.Dispose();
    }
}
