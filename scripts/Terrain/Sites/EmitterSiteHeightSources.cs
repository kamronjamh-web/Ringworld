using System;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Terrain.Sites;

/// <summary>Adapters exposing the unshaped height of each terrain backend.</summary>
public static class EmitterSiteHeightSources
{
    /// <summary>Creates the adapter matching the sampler's backend. The sampler stays owned by the caller.</summary>
    public static INaturalHeightSource Create(RingTerrainSampler sampler)
    {
        if (sampler == null) throw new ArgumentNullException(nameof(sampler));
        return sampler.UsesRuntimeSurfaceGeneration
            ? new Gen17NaturalHeightSource(TerrainGenerationParameters.FromJson(sampler.SurfaceGenerationRecipeJson), sampler.TerrainDefinitionHash)
            : new Gen16NaturalHeightSource(sampler, false);
    }
}

/// <summary>Gen-17 heightmap backend: <see cref="TerrainGenerationHeightModel"/> height with no site operator.</summary>
public sealed class Gen17NaturalHeightSource : INaturalHeightSource
{
    private readonly TerrainGenerationParameters _parameters;
    private readonly TerrainGenerationHeightModel _model;
    private readonly double _circumference;

    public Gen17NaturalHeightSource(TerrainGenerationParameters parameters, ulong identity)
    {
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _model = new TerrainGenerationHeightModel(parameters);
        _circumference = parameters.CircumferenceM;
        Identity = identity;
    }

    public double SeaLevel => 0.0;
    public double MinPossibleHeight => _parameters.MinHeightM;
    public ulong Identity { get; }

    public double NaturalHeight(long around, int across)
    {
        long length = (long)_circumference;
        long wrapped = around % length;
        if (wrapped < 0) wrapped += length;
        return _model.SampleDiagnostics(wrapped, across).Height;
    }

    public INaturalHeightSource CreateWorkerCopy() => new Gen17NaturalHeightSource(_parameters, Identity);
    public bool TryGetExclusionZone(out long around, out int across, out double radiusMeters)
    {
        around = 0; across = 0; radiusMeters = 0.0;
        return false;
    }

    public void Dispose() => _model.Dispose();
}

/// <summary>Gen-16 smooth backend: continental plus biome relief, before hydrology, plus the 0.5 presentation offset.</summary>
public sealed class Gen16NaturalHeightSource : INaturalHeightSource
{
    private readonly RingTerrainSampler _sampler;
    private readonly bool _ownsSampler;

    public Gen16NaturalHeightSource(RingTerrainSampler sampler, bool ownsSampler)
    {
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _ownsSampler = ownsSampler;
    }

    public double SeaLevel => _sampler.SeaLevelY;
    public double MinPossibleHeight => _sampler.MinimumNaturalSurfaceHeight + _sampler.SurfaceHeightOffset;
    public ulong Identity => _sampler.TerrainDefinitionHash;
    public double NaturalHeight(long around, int across) => _sampler.GetUnshapedDryPresentationHeight(around, across);
    public INaturalHeightSource CreateWorkerCopy() => new Gen16NaturalHeightSource(_sampler.CreateIndependentCopy(), true);

    public bool TryGetExclusionZone(out long around, out int across, out double radiusMeters)
    {
        around = 0; across = 0; radiusMeters = 0.0;
        if (!_sampler.HasExceptionalSummit) return false;
        (around, across) = _sampler.ExceptionalSummitCanonical;
        radiusMeters = _sampler.ExceptionalSummitRadiusMeters;
        return true;
    }

    public void Dispose()
    {
        if (_ownsSampler) _sampler.Dispose();
    }
}
