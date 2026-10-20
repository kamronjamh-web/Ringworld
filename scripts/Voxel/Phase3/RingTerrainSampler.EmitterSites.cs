using System;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>Read-only accessors for the emitter-site registry build. No generation behaviour lives here.</summary>
public sealed partial class RingTerrainSampler
{
    /// <summary>Lowest natural surface this sampler can produce (generation-16 clamp floor; runtime recipes use -8000).</summary>
    public int MinimumNaturalSurfaceHeight => UsesRuntimeSurfaceGeneration
        ? -8000
        : Math.Max(_settings.BedrockY + 1, _settings.DeepOceanFloorY);

    /// <summary>
    /// Emitter-site registry of a gen-17 v5 world (null for every other recipe). Built once per definition from the UNSHAPED
    /// natural height and shared by reference with every independent copy and worker model.
    /// </summary>
    public EmitterSiteRegistry? EmitterSites => _runtimeSurfaceModel?.EmitterSites?.Registry;

    /// <summary>Surface operator matching <see cref="EmitterSites"/> (null unless v5).</summary>
    public EmitterSiteSurfaceOperator? EmitterSiteOperator => _runtimeSurfaceModel?.EmitterSites?.Operator;

    /// <summary>
    /// Wall Engine climate machine registry of a gen-17 v6 world (null for every other recipe, including v5). Built once per definition from the
    /// UNSHAPED natural height and shared by reference with every independent copy and worker model.
    /// </summary>
    public ClimateMachineRegistry? ClimateMachines => _runtimeSurfaceModel?.EmitterSites?.Climate;

    /// <summary>
    /// Weather Lung placement of a gen-17 v8 world (null for every other recipe). Built once per definition from the UNSHAPED natural height and shared by
    /// reference with every independent copy and worker model. The world streamer places the POI at <see cref="WeatherLungPlacement.Site"/> with
    /// <c>EmitterSiteFrameContext.SiteTransform(Around, Across, PadHeight + clearance, CosYaw, SinYaw)</c>.
    /// </summary>
    public WeatherLungPlacement? WeatherLung => _runtimeSurfaceModel?.EmitterSites?.WeatherLung;

    /// <summary>Basin operator matching <see cref="WeatherLung"/> (null unless v8).</summary>
    public WeatherLungBasinOperator? WeatherLungOperator => _runtimeSurfaceModel?.EmitterSites?.WeatherLungOperator;

    /// <summary>Forecourt operator matching <see cref="ClimateMachines"/> (null unless v6).</summary>
    public ClimateForecourtOperator? ClimateForecourtOperator => _runtimeSurfaceModel?.EmitterSites?.ClimateOperator;

    /// <summary>
    /// Per-sample forecourt data of the shaped v6 terrain: influencing machine (-1 outside), influence weight and the surface mask.
    /// Returns false (and a default mask) for non-v6 worlds or points outside every machine's influence region.
    /// </summary>
    public bool TryGetClimateMachineSurfaceMask(long globalAroundVoxel, int globalAcrossVoxel, out SiteSurfaceMask mask, out int machineIndex, out double weight)
    {
        mask = default; machineIndex = -1; weight = 0d;
        if (_runtimeSurfaceModel?.EmitterSites?.Climate == null) return false;
        long around = RingCoordMath.WrapAround(globalAroundVoxel, _aroundRingVoxelLength);
        lock (_noiseLock)
        {
            TerrainGenerationHeightModel.Diagnostics d = _runtimeSurfaceModel.SampleDiagnostics(around, globalAcrossVoxel);
            mask = d.SiteMask; machineIndex = d.ClimateMachineIndex; weight = d.SiteWeight;
        }
        return machineIndex >= 0;
    }

    /// <summary>
    /// Per-sample site data of the shaped runtime terrain: influencing site (-1 outside), influence weight and the surface mask.
    /// Returns false (and a default mask) for non-v5 worlds or points outside every site's influence disc.
    /// </summary>
    public bool TryGetEmitterSiteSurfaceMask(long globalAroundVoxel, int globalAcrossVoxel, out SiteSurfaceMask mask, out int siteIndex, out double siteWeight)
    {
        mask = default; siteIndex = -1; siteWeight = 0d;
        if (_runtimeSurfaceModel?.EmitterSites == null) return false;
        long around = RingCoordMath.WrapAround(globalAroundVoxel, _aroundRingVoxelLength);
        lock (_noiseLock)
        {
            TerrainGenerationHeightModel.Diagnostics d = _runtimeSurfaceModel.SampleDiagnostics(around, globalAcrossVoxel);
            mask = d.SiteMask; siteIndex = d.SiteIndex; siteWeight = d.SiteWeight;
        }
        return siteIndex >= 0;
    }

    public float ExceptionalSummitRadiusMeters => _settings.ExceptionalSummitRadiusMeters;

    /// <summary>
    /// Generation-16 dry presentation height before hydrology (continental plus biome relief plus the
    /// 0.5 presentation offset). This is the value any future site shaping operator receives as h0.
    /// </summary>
    public float GetUnshapedDryPresentationHeight(long globalAroundVoxel, int globalAcrossVoxel)
    {
        if (UsesRuntimeSurfaceGeneration)
            throw new InvalidOperationException("Runtime surface generation exposes its unshaped height through the height model.");
        lock (_noiseLock)
        {
            long around = _periodicAroundRing
                ? RingCoordMath.WrapAround(globalAroundVoxel, _aroundRingVoxelLength)
                : globalAroundVoxel;
            RegionalCoordinate coordinate = ResolveRegionalCoordinate(around, globalAcrossVoxel);
            TerrainFields fields = SampleTerrainFieldsLocked(around, globalAcrossVoxel, coordinate);
            float preliminaryHeight = GetContinentalSurfaceHeightContinuousLocked(
                around, globalAcrossVoxel, coordinate, fields.Continentalness, fields.Erosion, fields.Mountain);
            return GetBiomeTerrainHeightLocked(coordinate, fields, preliminaryHeight).Height + SurfaceHeightOffset;
        }
    }
}
