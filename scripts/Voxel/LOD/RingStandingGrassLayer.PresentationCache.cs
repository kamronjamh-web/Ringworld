using System;
using Godot;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

// Local-transform notifications are immediate, including external Transform and
// GlobalTransform setters. The owning layer separately tracks ancestor motion.
internal partial class GrassPresentationRenderer : MultiMeshInstance3D
{
    internal long LocalRevision;
    internal Node? PresentationParent;
    internal MultiMesh? InstalledMultiMesh;
    internal int InstalledInstanceCount;
    internal int? CpuVisiblePrefix;
    internal bool? PresentationVisible;
    private RingStandingGrassLayer? _presentationOwner;

    public GrassPresentationRenderer() => SetNotifyLocalTransform(true);

    public override void _Notification(int what)
    {
        if (what == NotificationLocalTransformChanged)
        {
            LocalRevision++;
            _presentationOwner?.InvalidateGrassWork();
        }
        else if (what == NotificationParented || what == NotificationUnparented)
        {
            _presentationOwner?.InvalidateGrassWork();
            PresentationParent = GetParent();
            if (PresentationParent is RingStandingGrassLayer owner && !ReferenceEquals(_presentationOwner, owner))
            {
                _presentationOwner?.TrackGrassPresentationOwnership(this, false);
                _presentationOwner = owner;
            }
            _presentationOwner?.TrackGrassPresentationOwnership(this, PresentationParent != _presentationOwner);
            _presentationOwner?.InvalidateGrassWork();
            LocalRevision++;
        }
        else if (what == NotificationVisibilityChanged)
        {
            PresentationVisible = null;
            _presentationOwner?.InvalidateGrassWork();
        }
        else if (what == NotificationPredelete)
        {
            _presentationOwner?.TrackGrassPresentationOwnership(this, false);
            _presentationOwner = null;
        }
    }

    internal void RecordInstalledMultiMesh(MultiMesh mesh, int count)
    {
        InstalledMultiMesh = mesh;
        InstalledInstanceCount = count;
        CpuVisiblePrefix = null;
    }
}

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
        internal BoundsState? Bounds;
        internal Aabb RawWorldBounds, ExpandedWorldBounds;
        internal readonly RendererPresentationCache Near = new(), Intermediate = new();
    }

    internal sealed class RendererPresentationCache
    {
        internal MultiMeshInstance3D? Renderer;
        internal MappingState? Mapping;
        internal Vector2? GustUv;
        internal Vector4? TerrainOriginUv;
        internal bool? GpuCulled;
        internal long SnapshotRevision = -1;
        internal Transform3D WorldSnapshot;
        internal Transform3D LayerWorldSnapshot;
        internal Transform3D LocalSnapshot;
        internal long LocalRevision = -1;
    }

    internal readonly record struct MappingState(Vector3 Origin, Vector4 GustAround, Vector4 GustAcross,
        Vector4 ModifierAround, Vector4 ModifierAcross, int Slot);

    internal readonly record struct BoundsState(RingGrassTileData Data, Transform3D World,
        float MaximumHeight, float Padding, bool ReducedNear, bool SingleRibbonIntermediate);
    private long _presentationRevision;
    private Transform3D _presentationLayerWorld;
    private Transform3D _presentationInverseLayer;
    internal long PresentationWorldSnapshotReads { get; private set; }
    internal long PresentationBoundsBuilds { get; private set; }

    private Transform3D CapturePresentationWorld(RendererPresentationCache state, MultiMeshInstance3D renderer, Transform3D? parentWorld = null)
    {
        if (!ReferenceEquals(state.Renderer, renderer))
        {
            state.Renderer = renderer;
            state.Mapping = null;
            state.GustUv = null;
            state.TerrainOriginUv = null;
            state.GpuCulled = null;
            state.SnapshotRevision = -1;
            state.LocalRevision = -1;
        }
        Transform3D layerWorld = parentWorld ?? _presentationLayerWorld;
        bool owned = renderer is GrassPresentationRenderer tracked && tracked.PresentationParent == this;
        long localRevision = owned ? ((GrassPresentationRenderer)renderer).LocalRevision : -1;
        if (!owned || state.SnapshotRevision < 0 || state.LocalRevision != localRevision || state.LayerWorldSnapshot != layerWorld)
        {
            state.WorldSnapshot = renderer.GlobalTransform;
            state.LocalSnapshot = renderer.Transform;
            state.LocalRevision = localRevision;
            state.LayerWorldSnapshot = layerWorld;
            PresentationWorldSnapshotReads++;
        }
        state.SnapshotRevision = _presentationRevision;
        return state.WorldSnapshot;
    }

    private void RefreshPresentationBounds(TilePresentationCache cache, RingGrassTileData data,
        Transform3D world, float maximumHeight)
    {
        var state = new BoundsState(data, world, maximumHeight, GrassFrustumPaddingMeters,
            UseReducedNearGrassGeometry, IsSingleRibbonIntermediateGrassActive);
        if (cache.Bounds == state) return;
        cache.RawWorldBounds = world * LodAabb(data.LocalAabb);
        cache.ExpandedWorldBounds = ExpandPresentationBounds(cache.RawWorldBounds, world, maximumHeight);
        cache.Bounds = state;
        PresentationBoundsBuilds++;
    }

    internal readonly record struct PresentationAudit(long Around, long Across, ulong RendererId,
        bool Intermediate, Transform3D World, Aabb RawBounds, Aabb ExpandedBounds,
        float MaximumHeight, Transform3D? GpuFrameWorld);

    internal PresentationAudit[] CapturePresentationAuditsForTests()
    {
        var audits = new System.Collections.Generic.List<PresentationAudit>();
        foreach (var pair in _tiles)
        {
            var resident = pair.Value;
            if (resident.Renderer == null && resident.IntermediateRenderer == null) continue;
            void Add(RendererPresentationCache state, MultiMeshInstance3D? renderer, int batch, bool intermediate)
            {
                if (renderer == null || state.SnapshotRevision != _presentationRevision) return;
                Transform3D? frameWorld = null;
                foreach (var frame in _gpuFrames)
                    if (frame.BatchId == batch) { frameWorld = frame.LocalToWorld; break; }
                audits.Add(new PresentationAudit(pair.Key.Around, pair.Key.Across, renderer.GetInstanceId(),
                    intermediate, state.WorldSnapshot, resident.Presentation.RawWorldBounds,
                    resident.Presentation.ExpandedWorldBounds, resident.MaximumSourceHeight, frameWorld));
            }
            Add(resident.Presentation.Near, resident.Renderer, resident.GpuNearBatch, false);
            Add(resident.Presentation.Intermediate, resident.IntermediateRenderer, resident.GpuIntermediateBatch, true);
        }
        return audits.ToArray();
    }

    internal long PresentationMappingBuilds { get; private set; }
    internal long PresentationParameterWrites { get; private set; }

    internal Transform3D RefreshTilePresentation(TilePresentationCache cache, MultiMeshInstance3D renderer,
        RingGrassTileData data, RingLodGeometrySnapshot geometry, Transform3D inverseLayer, bool intermediate)
    {
        RefreshCanonicalTilePresentation(cache, data, geometry);
        RendererPresentationCache state = intermediate ? cache.Intermediate : cache.Near;
        bool owned = renderer is GrassPresentationRenderer tracked && tracked.PresentationParent == this;
        // External reparenting retains the canonical world anchor. Use the
        // actual transform parent when restoring its local transform.
        Transform3D inverseParent = owned ? inverseLayer
            : renderer.GetParentNode3D()?.GlobalTransform.AffineInverse() ?? Transform3D.Identity;
        Transform3D local = inverseParent * cache.World;
        bool localKnown = owned && ReferenceEquals(state.Renderer, renderer) && state.LocalRevision == ((GrassPresentationRenderer)renderer).LocalRevision && state.SnapshotRevision >= 0;
        Transform3D actualLocal = localKnown ? state.LocalSnapshot : renderer.Transform;
        if (actualLocal != local)
        {
            renderer.Transform = local;
            state.SnapshotRevision = -1;
        }
        Transform3D parentWorld = inverseLayer == _presentationInverseLayer ? _presentationLayerWorld : inverseLayer.AffineInverse();
        Transform3D world = CapturePresentationWorld(state, renderer, parentWorld);
        state.LocalSnapshot = local;

        long circumference = Math.Max(1L, (long)Math.Round(geometry.CircumferenceMeters));
        GetGrassGustPeriods(circumference, out double aroundPeriod, out double acrossPeriod);
        GrassGustEffectiveAroundScaleMeters = UsesSharedWindField ? (float)_grassGustWeather!.GustAroundPeriodMeters : (float)aroundPeriod;
        Vector3 origin = world.Origin;
        int slot = cache.Valid && _grassModifierSlots.TryGetValue(data.Key, out int assigned) ? assigned : -1;
        var mapping = new MappingState(origin,
            new Vector4(cache.Around.X, cache.Around.Y, cache.Around.Z, cache.Valid ? (float)(1d / aroundPeriod) : 0),
            new Vector4(cache.Across.X, cache.Across.Y, cache.Across.Z, cache.Valid ? (float)(1d / acrossPeriod) : 0),
            new Vector4(cache.Around.X, cache.Around.Y, cache.Around.Z, (float)(1d / Math.Max(.001, data.Bounds.AroundMax - data.Bounds.AroundMin))),
            new Vector4(cache.Across.X, cache.Across.Y, cache.Across.Z, (float)(1d / Math.Max(.001, data.Bounds.AcrossMax - data.Bounds.AcrossMin))), slot);
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
        if (_style.IsCards)
        {
            // Canonical (unchanged by floating-origin rebases): written once per renderer, never per frame.
            Vector4 terrainUv = CardTerrainOriginUv(data.Bounds, geometry.RingRadiusMeters, _ringAcrossWidthMeters, geometry.CircumferenceMeters, _cardFocusAroundMeters);
            if (state.TerrainOriginUv != terrainUv)
            {
                renderer.SetInstanceShaderParameter("grass_terrain_origin_uv", terrainUv);
                state.TerrainOriginUv = terrainUv;
                PresentationParameterWrites++;
            }
        }
        return world;
    }

    // Canonical mapping survives output retirement and can conservatively test
    // camera visibility before allocating another native MultiMesh.
    private void RefreshCanonicalTilePresentation(TilePresentationCache cache,
        RingGrassTileData data, RingLodGeometrySnapshot geometry)
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
    }

    private static int PresentationInstanceCount(MultiMeshInstance3D renderer)
        => renderer is GrassPresentationRenderer tracked && tracked.InstalledMultiMesh != null
            ? tracked.InstalledInstanceCount : renderer.Multimesh?.InstanceCount ?? 0;

    private static void SetPresentationPrefix(MultiMeshInstance3D renderer, int prefix)
    {
        if (renderer is GrassPresentationRenderer tracked && tracked.InstalledMultiMesh != null)
        {
            if (tracked.CpuVisiblePrefix != prefix)
            {
                if (tracked.CpuVisiblePrefix.HasValue || tracked.InstalledMultiMesh.VisibleInstanceCount != prefix)
                    tracked.InstalledMultiMesh.VisibleInstanceCount = prefix;
                tracked.CpuVisiblePrefix = prefix;
            }
        }
        else if (renderer.Multimesh is { } mesh && mesh.VisibleInstanceCount != prefix) mesh.VisibleInstanceCount = prefix;
    }
}
