using System;
using Godot;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    // Owned by the resident tile: eviction releases the cache with the tile.
    // Wind UVs remain dynamic; only the ground mapping and unchanged writes are cached.
    internal sealed class TilePresentationCache
    {
        internal RingGrassTileData? Data;
        internal RingLodGeometrySnapshot Geometry;
        internal Transform3D World;
        internal Vector3 Around, Across;
        internal bool Valid;
        internal readonly RendererPresentationCache Near = new(), Intermediate = new();
    }

    internal sealed class RendererPresentationCache
    {
        internal MultiMeshInstance3D? Renderer;
        internal MappingState? Mapping;
        internal Vector2? GustUv;
        internal bool? GpuCulled;
    }

    internal readonly record struct MappingState(Vector3 Origin, Vector4 GustAround, Vector4 GustAcross,
        Vector4 ModifierAround, Vector4 ModifierAcross, int Slot);

    internal long PresentationMappingBuilds { get; private set; }
    internal long PresentationParameterWrites { get; private set; }

    internal void RefreshTilePresentation(TilePresentationCache cache, MultiMeshInstance3D renderer,
        RingGrassTileData data, RingLodGeometrySnapshot geometry, Transform3D inverseLayer, bool intermediate)
    {
        if (!ReferenceEquals(cache.Data, data) || cache.Geometry != geometry)
        {
            cache.Data = data;
            cache.Geometry = geometry;
            cache.World = RingGrassCoordinates.CreateTileWorldTransform(data.Bounds, data.AnchorHeight, geometry);
            cache.Valid = CalculateGrassGroundCovectors(geometry, data.AnchorHeight, data.Bounds.AroundCenter,
                out cache.Around, out cache.Across);
            PresentationMappingBuilds++;
        }
        Transform3D local = inverseLayer * cache.World;
        if (renderer.Transform != local) renderer.Transform = local;

        long circumference = Math.Max(1L, (long)Math.Round(geometry.CircumferenceMeters));
        GetGrassGustPeriods(circumference, out double aroundPeriod, out double acrossPeriod);
        GrassGustEffectiveAroundScaleMeters = (float)aroundPeriod;
        Vector3 origin = renderer.GlobalTransform.Origin;
        int slot = cache.Valid && _grassModifierSlots.TryGetValue(data.Key, out int assigned) ? assigned : -1;
        var mapping = new MappingState(origin,
            new Vector4(cache.Around.X, cache.Around.Y, cache.Around.Z, cache.Valid ? (float)(1d / aroundPeriod) : 0),
            new Vector4(cache.Across.X, cache.Across.Y, cache.Across.Z, cache.Valid ? (float)(1d / acrossPeriod) : 0),
            new Vector4(cache.Around.X, cache.Around.Y, cache.Around.Z, (float)(1d / Math.Max(.001, data.Bounds.AroundMax - data.Bounds.AroundMin))),
            new Vector4(cache.Across.X, cache.Across.Y, cache.Across.Z, (float)(1d / Math.Max(.001, data.Bounds.AcrossMax - data.Bounds.AcrossMin))), slot);
        RendererPresentationCache state = intermediate ? cache.Intermediate : cache.Near;
        if (!ReferenceEquals(state.Renderer, renderer))
        {
            state.Renderer = renderer;
            state.Mapping = null;
            state.GustUv = null;
            state.GpuCulled = null;
        }
        if (state.Mapping != mapping)
        {
            renderer.SetInstanceShaderParameter("grass_gust_origin_world", mapping.Origin);
            renderer.SetInstanceShaderParameter("grass_gust_around_world", mapping.GustAround);
            renderer.SetInstanceShaderParameter("grass_gust_across_world", mapping.GustAcross);
            renderer.SetInstanceShaderParameter("grass_modifier_layer", mapping.Slot);
            renderer.SetInstanceShaderParameter("grass_modifier_origin_world", mapping.Origin);
            renderer.SetInstanceShaderParameter("grass_modifier_around_world", mapping.ModifierAround);
            renderer.SetInstanceShaderParameter("grass_modifier_across_world", mapping.ModifierAcross);
            state.Mapping = mapping;
            PresentationParameterWrites += 7;
        }
        Vector2 uv = _grassGustState.OriginUv(data.Bounds.AroundCenter, data.Bounds.AcrossCenter, aroundPeriod, acrossPeriod);
        if (state.GustUv != uv)
        {
            renderer.SetInstanceShaderParameter("grass_gust_origin_uv", uv);
            state.GustUv = uv;
            PresentationParameterWrites++;
        }
    }
}
