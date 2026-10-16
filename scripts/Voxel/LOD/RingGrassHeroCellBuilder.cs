using System;
using System.Threading;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Worker-only builder of one hero-blade cell: the short solid-blade tuft layer drawn on top of the cards within a few
/// metres of the player. It runs the identical TryPlace core as the card/blade builder (BlocksVegetation, biome chance,
/// slope/soil/resolver, water, modifiers, exclusions) on a 4 m cell at the hero tuft density. A cell depends only on
/// its key, the terrain and the revisions it is given: never on the camera, focus, floating origin or time. There is
/// no tier key, so the data cannot be tier-filtered; the layer rebuilds or evicts whole cells.
/// </summary>
internal static class RingGrassHeroCellBuilder
{
    public const float DefaultCellMeters = 4f;
    public const float DefaultDensity = 50f;
    public const float DefaultHeightScale = .9f;

    public static RingGrassTileData Build(RingGrassTileKey cell, float cellMeters, float tuftsPerSquareMeter, float heightScale,
        int maxInstances, float maxSlope, RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits, RingLodGeometrySnapshot geometry,
        CancellationToken cancellation, SmoothTerrainVolumetricDensitySnapshot? field = null, RingGrassExclusionSnapshot? exclusions = null,
        RingGrassAppearanceSnapshot appearance = default, RingGrassSurfaceCache? surfaceCache = null,
        RingGrassModifierSnapshot? modifiers = null, float modifierDensityStrength = 1)
    {
        cellMeters = float.IsFinite(cellMeters) ? Math.Clamp(cellMeters, 1f, 32f) : DefaultCellMeters;
        RingGrassTileBounds bounds = RingGrassCoordinates.Bounds(cell.Around, cell.Across, cellMeters,
            Math.Max(1L, sampler.AroundRingVoxelLength), Math.Max(1, sampler.RingAcrossWidthMeters));
        var options = new RingGrassCardPlacement
        {
            Hero = true,
            HeroDensity = tuftsPerSquareMeter,
            HeroHeightScale = float.IsFinite(heightScale) ? Math.Clamp(heightScale, .1f, 2f) : DefaultHeightScale,
        };
        return RingGrassTileBuilder.Build(cell, bounds, cellMeters, 1, 0, maxInstances, maxSlope, sampler, edits, geometry, cancellation,
            field, exclusions, appearance, surfaceCache: surfaceCache, modifiers: modifiers, modifierDensityStrength: modifierDensityStrength, cards: options);
    }
}
